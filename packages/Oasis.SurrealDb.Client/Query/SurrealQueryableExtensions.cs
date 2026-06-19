// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Client.Query -- async materializers for the deferred
// SurrealQueryable<T> (surreal-linq-graph-query Phase 2). Each folds the
// accumulated expression tree into ONE SurrealQuery<T> and dispatches via the
// ISurrealExecutor. These are the only sanctioned execution entry points
// (IQueryProvider.Execute throws to steer callers here).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Oasis.SurrealDb.Client.Query
{
    /// <summary>Async terminal operators for <see cref="SurrealQueryable{T}"/>.</summary>
    public static class SurrealQueryableExtensions
    {
        /// <summary>
        /// Project the SurrealQL field list (<c>SELECT id, status FROM …</c>)
        /// while KEEPING the element type <typeparamref name="T"/> — SurrealDB
        /// returns rows with only the projected columns populated, the rest at
        /// their CLR default. This is distinct from LINQ's
        /// <c>Queryable.Select</c>, which changes the element type to the
        /// projection shape; the deferred SurrealDB surface stays
        /// <see cref="ISurrealRecord"/>-typed end to end so the materializers'
        /// constraint holds. Compose before WHERE/ORDER BY, like the eager
        /// <see cref="SurrealQuery{T}.Select{TResult}"/>.
        /// </summary>
        public static SurrealQueryable<T> SelectFields<T, TResult>(
            this SurrealQueryable<T> source, Expression<Func<T, TResult>> projection)
            where T : ISurrealRecord, new()
        {
            if (projection is null) throw new ArgumentNullException(nameof(projection));
            // Append a synthetic call node the translator routes to
            // SurrealQuery<T>.Select. MethodInfo is resolved off this method so
            // the translator can recognize it by name ("SelectFields").
            var method = typeof(SurrealQueryableExtensions)
                .GetMethod(nameof(SelectFields))!
                .MakeGenericMethod(typeof(T), typeof(TResult));
            var call = Expression.Call(
                method,
                source.Expression,
                Expression.Quote(projection));
            return new SurrealQueryable<T>((SurrealQueryProvider)source.Provider, call);
        }

        /// <summary>Materialize all matching rows.</summary>
        public static async Task<List<T>> ToListAsync<T>(
            this IQueryable<T> source, CancellationToken ct = default)
            where T : ISurrealRecord, new()
        {
            var (executor, query) = Resolve(source);
            var rows = await executor.QueryAsync<T>(query, ct).ConfigureAwait(false);
            return rows is List<T> list ? list : rows.ToList();
        }

        /// <summary>First matching row, or <c>default</c> when none match (adds LIMIT 1).</summary>
        public static async Task<T?> FirstOrDefaultAsync<T>(
            this IQueryable<T> source, CancellationToken ct = default)
            where T : ISurrealRecord, new()
        {
            var (executor, query) = Resolve(source);
            var limited = query.Limit(1);
            var rows = await executor.QueryAsync<T>(limited, ct).ConfigureAwait(false);
            return rows.Count > 0 ? rows[0] : default;
        }

        /// <summary>
        /// Single matching row or <c>null</c>; throws if more than one row
        /// matches. Adds <c>LIMIT 2</c> so the over-count is detectable without
        /// pulling the whole set.
        /// </summary>
        public static async Task<T?> SingleOrDefaultAsync<T>(
            this IQueryable<T> source, CancellationToken ct = default)
            where T : ISurrealRecord, new()
        {
            var (executor, query) = Resolve(source);
            var limited = query.Limit(2);
            var rows = await executor.QueryAsync<T>(limited, ct).ConfigureAwait(false);
            if (rows.Count > 1)
                throw new InvalidOperationException(
                    "SingleOrDefaultAsync matched more than one row.");
            return rows.Count == 1 ? rows[0] : default;
        }

        /// <summary>Count of matching rows via SurrealDB <c>count()</c> + <c>GROUP ALL</c>.</summary>
        public static async Task<long> CountAsync<T>(
            this IQueryable<T> source, CancellationToken ct = default)
            where T : ISurrealRecord, new()
        {
            var (executor, query) = Resolve(source);
            var countQuery = BuildCountQuery(query);
            var rows = await executor.QueryAsync<CountRow>(countQuery, ct).ConfigureAwait(false);
            return rows.Count > 0 ? rows[0].Count : 0L;
        }

        /// <summary>True iff at least one row matches (count &gt; 0).</summary>
        public static async Task<bool> AnyAsync<T>(
            this IQueryable<T> source, CancellationToken ct = default)
            where T : ISurrealRecord, new()
            => await CountAsync(source, ct).ConfigureAwait(false) > 0;

        // ─── helpers ────────────────────────────────────────────────────────

        private static (ISurrealExecutor, SurrealQuery<T>) Resolve<T>(IQueryable<T> source)
            where T : ISurrealRecord, new()
        {
            if (source is not SurrealQueryable<T> sq)
                throw new NotSupportedException(
                    "The async SurrealDB materializers require a SurrealQueryable<T> source " +
                    "(obtained from SurrealContext.Set<T>() or a SurrealQueryProvider). " +
                    "Got: " + source.GetType().Name + ".");
            return (sq.SurrealProvider.Executor, sq.BuildQuery());
        }

        /// <summary>
        /// Rewrite a folded <c>SELECT … FROM t [WHERE …]</c> into
        /// <c>SELECT count() AS c FROM t [WHERE …] GROUP ALL</c>, preserving the
        /// WHERE predicate + its bound parameters. ORDER BY / LIMIT / START are
        /// dropped (meaningless under an aggregate count).
        /// </summary>
        private static SurrealQuery BuildCountQuery<T>(SurrealQuery<T> typed)
            where T : ISurrealRecord, new()
        {
            var inner = typed.AsUntyped();
            var sql = inner.Sql;

            // Isolate the FROM clause + any trailing WHERE; strip ORDER/LIMIT/START.
            const string fromMarker = " FROM ";
            int fromIdx = sql.IndexOf(fromMarker, StringComparison.Ordinal);
            if (fromIdx < 0)
                throw new NotSupportedException("CountAsync could not locate a FROM clause to aggregate.");

            var afterFrom = sql.Substring(fromIdx + fromMarker.Length);
            // Cut everything from the first ORDER BY / LIMIT / START onward.
            foreach (var tail in new[] { " ORDER BY ", " LIMIT ", " START " })
            {
                int idx = afterFrom.IndexOf(tail, StringComparison.Ordinal);
                if (idx >= 0) afterFrom = afterFrom.Substring(0, idx);
            }

            var countSql = "SELECT count() AS c FROM " + afterFrom.TrimEnd() + " GROUP ALL";
            return SurrealQuery.Of(countSql).WithParams(inner.Params);
        }

        /// <summary>Projection for <c>SELECT count() AS c … GROUP ALL</c>.</summary>
        private sealed class CountRow
        {
            [System.Text.Json.Serialization.JsonPropertyName("c")]
            public long Count { get; set; }
        }
    }
}
