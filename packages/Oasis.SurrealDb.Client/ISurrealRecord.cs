// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Client -- marker interface implemented by every generated
// SurrealDB POCO (via [[surrealdb-schema-source-gen]] Oasis.SurrealDb.SourceGen).
//
// The interface exposes the SurrealDB table name as an instance property
// (netstandard2.0 cannot host `static abstract` members on interfaces).
// `RecordId<T>` and `SurrealQuery<T>` rely on this marker + a runtime
// SchemaName lookup to bind their type parameter to a concrete table name.

namespace Oasis.SurrealDb.Client
{
    /// <summary>
    /// Marker contract implemented by every generated SurrealDB POCO. The
    /// <see cref="SchemaName"/> instance property reflects the SurrealDB
    /// table name the POCO maps to; the type-system pin used by
    /// <see cref="RecordId{T}"/> and <c>SurrealQuery&lt;T&gt;</c> defers the
    /// table-name lookup to a single <c>new T()</c> instantiation cached at
    /// type-resolution time.
    /// </summary>
    /// <remarks>
    /// Why an instance property instead of a static one: netstandard2.0
    /// (the target framework for the Oasis.SurrealDb suite and any
    /// downstream package consumer) does not support C# 11's
    /// <c>static abstract</c> interface members. A cached
    /// <c>new T().SchemaName</c> lookup keyed by <see cref="System.Type"/>
    /// preserves the same zero-runtime-cost shape after first call.
    /// </remarks>
    public interface ISurrealRecord
    {
        /// <summary>
        /// The SurrealDB table this record type maps to (e.g. <c>"wallet"</c>).
        /// Generated implementations return a string constant.
        /// </summary>
        string SchemaName { get; }
    }
}
