// SPDX-License-Identifier: UNLICENSED
// AZOA.WebAPI.Tests -- orphan-golden acceptance gate, the reverse direction
// of AttributePocoByteEquivalenceTests.cs.
//
// Acceptance contract:
//   For every committed .surql under Persistence/SurrealDb/Generated/Schemas/,
//   its table name MUST be either (a) backed by a [SurrealTable]-decorated
//   POCO in the AZOA.WebAPI assembly, or (b) named in the explicit
//   AllowlistedOrphanTables list below with a non-empty reason.
//
// This closes OI-76: kyc_authority_guard.surql was a real, live table with
// zero attributed POCO, and AttributePocoByteEquivalenceTests only iterates
// POCOs -> goldens, so an orphan golden with no POCO was never inspected in
// either direction and passed silently. This test walks goldens -> POCOs,
// the direction that catches that gap, and fails the build on any FUTURE
// unallowlisted orphan the same way.
//
// The allowlist is kept honest in both directions: a stale entry (no
// matching golden, or a table that has since gained a POCO) fails the build
// too, so this cannot become a place to silently accumulate exceptions --
// see RUNBOOK.md 2 ("a claim in prose is not a control in code") for why
// that distinction matters here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Tests.Persistence
{
    public class SchemaGoldenOrphanTests
    {
        /// <summary>
        /// Tables whose committed .surql has no attributed POCO yet. Every entry needs a
        /// real reason and an unblock path -- this is a tracked-debt list, not a place to
        /// silence a finding. Empty as of OI-76: the one known orphan, kyc_authority_guard,
        /// was given a POCO (Persistence/SurrealDb/Models/KycAuthorityGuard.cs) instead of
        /// being allowlisted.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> AllowlistedOrphanTables =
            new Dictionary<string, string>(StringComparer.Ordinal);

        [Fact]
        public void Every_committed_golden_has_an_attributed_poco_or_a_documented_allowlist_entry()
        {
            var schemasDir = ResolveSchemasDir();
            Directory.Exists(schemasDir).Should().BeTrue(
                $"expected the committed schema directory at {schemasDir}");

            var goldenTables = Directory.GetFiles(schemasDir, "*.surql")
                .Select(f => Path.GetFileNameWithoutExtension(f)!)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();
            goldenTables.Should().NotBeEmpty();

            var attributedTables = AllAttributedTableNames();

            var orphans = goldenTables
                .Where(t => !attributedTables.Contains(t) && !AllowlistedOrphanTables.ContainsKey(t))
                .ToList();
            orphans.Should().BeEmpty(
                "every committed .surql under Generated/Schemas/ must be backed by a " +
                "[SurrealTable] POCO or an explicit, reasoned entry in " +
                $"{nameof(AllowlistedOrphanTables)} (SchemaGoldenOrphanTests.cs); found " +
                $"unattributed, unallowlisted golden(s): {string.Join(", ", orphans)}");

            foreach (var entry in AllowlistedOrphanTables)
            {
                goldenTables.Should().Contain(entry.Key,
                    $"{nameof(AllowlistedOrphanTables)} names '{entry.Key}' but no matching " +
                    "committed .surql exists under Generated/Schemas/ -- remove the stale entry");
                attributedTables.Should().NotContain(entry.Key,
                    $"'{entry.Key}' now has an attributed POCO -- remove it from " +
                    $"{nameof(AllowlistedOrphanTables)} instead of carrying a redundant exception");
                entry.Value.Should().NotBeNullOrWhiteSpace(
                    $"{nameof(AllowlistedOrphanTables)}['{entry.Key}'] must document why it is exempt");
            }
        }

        // ─── helpers ──────────────────────────────────────────────────────

        private static HashSet<string> AllAttributedTableNames()
        {
            var asm = typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.Wallet).Assembly;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in asm.GetTypes())
            {
                var table = t.GetCustomAttribute<SurrealTableAttribute>(inherit: false);
                if (table != null) names.Add(table.Name);
            }
            return names;
        }

        private static string ResolveSchemasDir()
        {
            // Walk upward from the test bin until we find the Generated/Schemas
            // directory at the repo root (mirrors FlowchartRegenerationTests.cs).
            var probe = AppContext.BaseDirectory;
            for (int hop = 0; hop < 12; hop++)
            {
                var candidate = Path.Combine(
                    probe, "Persistence", "SurrealDb", "Generated", "Schemas");
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
                var parent = Directory.GetParent(probe);
                if (parent == null) break;
                probe = parent.FullName;
            }
            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "Persistence", "SurrealDb", "Generated", "Schemas"));
        }
    }
}
