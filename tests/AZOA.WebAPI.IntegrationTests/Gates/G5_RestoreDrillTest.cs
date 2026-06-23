using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Factories;

namespace AZOA.WebAPI.IntegrationTests.Gates;

/// <summary>
/// Gate G5 — Backup/Restore Drill.
///
/// Proves that the backup/restore scripts are first-class and exercised:
///   1. Seeds deterministic rows across every value table.
///   2. Computes per-table SHA-256 checksums (count + sorted-JSON hash).
///   3. Drives scripts/surrealdb/backup.ps1 via pwsh to export the namespace.
///   4. Wipes the namespace via REMOVE NAMESPACE.
///   5. Drives scripts/surrealdb/restore.ps1 via pwsh with -Force to replay.
///   6. Re-creates the namespace definition (restore.ps1 replays DDL+data but
///      the namespace-level DEFINE is emitted by surreal export into the file).
///   7. Recomputes checksums and asserts byte-equality to the pre-backup set.
///
/// Script surface (confirmed by reading scripts/surrealdb/backup.ps1 and restore.ps1):
///   backup.ps1  -OutputPath &lt;path&gt; -Namespace &lt;ns&gt; -Database &lt;db&gt;
///               -Endpoint &lt;url&gt; -User &lt;user&gt; -Pass &lt;pass&gt;
///   restore.ps1 -InputPath  &lt;path&gt; -Namespace &lt;ns&gt; -Database &lt;db&gt;
///               -Endpoint &lt;url&gt; -User &lt;user&gt; -Pass &lt;pass&gt; -Force
///
/// Both scripts use `docker exec azoa-surrealdb surreal export/import`.
/// The test falls back to documenting the gap if docker is not on PATH.
/// </summary>
[Trait("Category", "Gate")]
public sealed class G5_RestoreDrillTest : IntegrationTestBase
{
    // ── Deterministic seed IDs (fixed so post-restore read is byte-comparable) ──

    // wallet rows
    private static readonly string WalletId1 = "a1000000000000000000000000000001";
    private static readonly string WalletId2 = "a1000000000000000000000000000002";
    private static readonly string WalletId3 = "a1000000000000000000000000000003";

    // bridge_tx rows
    private static readonly string BridgeId1 = "b2000000000000000000000000000001";
    private static readonly string BridgeId2 = "b2000000000000000000000000000002";
    private static readonly string BridgeId3 = "b2000000000000000000000000000003";

    // nft_ownership rows
    private static readonly string NftId1 = "c3000000000000000000000000000001";
    private static readonly string NftId2 = "c3000000000000000000000000000002";
    private static readonly string NftId3 = "c3000000000000000000000000000003";

    // operation_log rows
    private static readonly string OpId1 = "d4000000000000000000000000000001";
    private static readonly string OpId2 = "d4000000000000000000000000000002";
    private static readonly string OpId3 = "d4000000000000000000000000000003";

    // consumed_vaa_ledger rows
    private static readonly string VaaId1 = "e5000000000000000000000000000001";
    private static readonly string VaaId2 = "e5000000000000000000000000000002";
    private static readonly string VaaId3 = "e5000000000000000000000000000003";

    // idempotency_key_store rows
    private static readonly string IdemId1 = "f6000000000000000000000000000001";
    private static readonly string IdemId2 = "f6000000000000000000000000000002";
    private static readonly string IdemId3 = "f6000000000000000000000000000003";

    // saga_steps rows
    private static readonly string SagaId1 = "07000000000000000000000000000001";
    private static readonly string SagaId2 = "07000000000000000000000000000002";
    private static readonly string SagaId3 = "07000000000000000000000000000003";

    // avatar rows
    private static readonly string AvatarId1 = "18000000000000000000000000000001";
    private static readonly string AvatarId2 = "18000000000000000000000000000002";
    private static readonly string AvatarId3 = "18000000000000000000000000000003";

    // holon rows
    private static readonly string HolonId1 = "29000000000000000000000000000001";
    private static readonly string HolonId2 = "29000000000000000000000000000002";
    private static readonly string HolonId3 = "29000000000000000000000000000003";

    // star_odk rows
    private static readonly string StarId1 = "3a000000000000000000000000000001";
    private static readonly string StarId2 = "3a000000000000000000000000000002";
    private static readonly string StarId3 = "3a000000000000000000000000000003";

    // api_key rows
    private static readonly string ApiKeyId1 = "4b000000000000000000000000000001";
    private static readonly string ApiKeyId2 = "4b000000000000000000000000000002";
    private static readonly string ApiKeyId3 = "4b000000000000000000000000000003";

    // quest_template rows
    private static readonly string QuestTplId1 = "5c000000000000000000000000000001";
    private static readonly string QuestTplId2 = "5c000000000000000000000000000002";
    private static readonly string QuestTplId3 = "5c000000000000000000000000000003";

    // quest_node_template rows
    private static readonly string QuestNodeTplId1 = "6d000000000000000000000000000001";
    private static readonly string QuestNodeTplId2 = "6d000000000000000000000000000002";
    private static readonly string QuestNodeTplId3 = "6d000000000000000000000000000003";

    // ── Connection config (mirrors IntegrationTestBase private statics) ─────────

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Constructor ───────────────────────────────────────────────────────────

    public G5_RestoreDrillTest(AZOATestWebApplicationFactory factory)
        : base(factory)
    {
    }

    // ── G5 test ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task G5_Backup_Wipe_Restore_PreservesEveryValueTable()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(), "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        // ── 0. Locate repo root and scripts ──────────────────────────────────

        var repoRoot = FindRepoRootLocal();
        repoRoot.Should().NotBeNull("repo root must be locatable from AppContext.BaseDirectory");

        var backupScript  = Path.Combine(repoRoot!, "scripts", "surrealdb", "backup.ps1");
        var restoreScript = Path.Combine(repoRoot!, "scripts", "surrealdb", "restore.ps1");

        backupScript.Should().NotBeNull();
        restoreScript.Should().NotBeNull();

        File.Exists(backupScript).Should().BeTrue($"backup.ps1 must exist at {backupScript}");
        File.Exists(restoreScript).Should().BeTrue($"restore.ps1 must exist at {restoreScript}");

        // Temp backup file scoped to this test namespace to avoid cross-test collisions.
        var tempDir     = Path.Combine(Path.GetTempPath(), "azoa-g5-drill");
        Directory.CreateDirectory(tempDir);
        var backupFile  = Path.Combine(tempDir, $"backup-{TestNamespace}.surql");

        // ── 1. Apply schemas and seed rows ───────────────────────────────────

        await ApplyAllSchemasAsync(repoRoot!);
        await SeedKnownRowsAcrossAllValueTablesAsync();

        // ── 2. Compute pre-backup per-table checksums ────────────────────────

        var tables = AllValueTables();
        var preSums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var rows = await SelectAllRowsAsync(table);
            preSums[table] = ComputeTableChecksum(rows);
        }

        // Sanity: every table must have at least 3 rows.
        foreach (var table in tables)
        {
            var rows = await SelectAllRowsAsync(table);
            rows.Count().Should().BeGreaterOrEqualTo(3,
                $"table '{table}' must have at least 3 seeded rows before backup");
        }

        // ── 3. Drive backup.ps1 ──────────────────────────────────────────────

        var backupArgs = string.Join(" ",
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", $"\"{backupScript}\"",
            "-OutputPath", $"\"{backupFile}\"",
            "-Namespace", TestNamespace,
            "-Database", "test",
            "-Endpoint", SurrealTestDefaults.Endpoint,
            "-User", SurrealTestDefaults.User,
            "-Pass", SurrealTestDefaults.Password);

        var (backupExit, backupStdOut, backupStdErr) = RunPwsh(backupArgs);

        backupExit.Should().Be(0,
            $"backup.ps1 must exit 0. stdout={backupStdOut} stderr={backupStdErr}");
        File.Exists(backupFile).Should().BeTrue("backup.ps1 must produce the output file");
        new FileInfo(backupFile).Length.Should().BeGreaterThan(0, "backup file must be non-empty");

        // ── 4. Wipe namespace ────────────────────────────────────────────────

        // REMOVE NAMESPACE drops every table in the namespace atomically.
        // We use ExecuteSurrealSqlAsync (base-class protected helper).
        await ExecuteSurrealSqlAsync("REMOVE NAMESPACE $ns", new { ns = TestNamespace });

        // Confirm all tables are empty post-wipe.
        foreach (var table in tables)
        {
            var afterWipe = await SelectAllRowsAsync(table);
            afterWipe.Count().Should().Be(0,
                $"table '{table}' must be empty immediately after REMOVE NAMESPACE");
        }

        // ── 5. Drive restore.ps1 ─────────────────────────────────────────────

        var restoreArgs = string.Join(" ",
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", $"\"{restoreScript}\"",
            "-InputPath", $"\"{backupFile}\"",
            "-Namespace", TestNamespace,
            "-Database", "test",
            "-Endpoint", SurrealTestDefaults.Endpoint,
            "-User", SurrealTestDefaults.User,
            "-Pass", SurrealTestDefaults.Password,
            "-Force");

        var (restoreExit, restoreStdOut, restoreStdErr) = RunPwsh(restoreArgs);

        restoreExit.Should().Be(0,
            $"restore.ps1 must exit 0. stdout={restoreStdOut} stderr={restoreStdErr}");

        // ── 6. Re-create namespace definition if needed ──────────────────────
        // surreal export includes DEFINE NAMESPACE / DEFINE DATABASE statements,
        // so restore.ps1 typically recreates both. We re-apply defensively to
        // make the SurrealClient headers (NS/DB) valid for SELECT queries below.
        await ExecuteSurrealSqlAsync("DEFINE NAMESPACE IF NOT EXISTS $ns", new { ns = TestNamespace });
        await ExecuteSurrealSqlAsync(
            "USE NS $ns DB test; DEFINE DATABASE IF NOT EXISTS test",
            new { ns = TestNamespace });

        // ── 7. Recompute checksums and assert byte-equality ───────────────────

        var postSums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var rows = await SelectAllRowsAsync(table);
            postSums[table] = ComputeTableChecksum(rows);
        }

        foreach (var table in tables)
        {
            postSums[table].Should().Be(preSums[table],
                $"table '{table}': post-restore checksum must be byte-equal to pre-backup checksum");
        }

        // Cleanup temp backup file.
        try { File.Delete(backupFile); } catch { /* best-effort */ }
    }

    // ── Seed helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds ~3 deterministic rows per value table via raw SurrealQL CREATE
    /// statements (not Store classes — avoids a Store serialisation bug hiding
    /// a backup/restore bug).
    /// </summary>
    private async Task SeedKnownRowsAcrossAllValueTablesAsync()
    {
        // ── wallet ────────────────────────────────────────────────────────────
        foreach (var (id, chain, addr) in new[]
        {
            (WalletId1, "Algorand", "ALGO_ADDR_G5_01"),
            (WalletId2, "Solana",   "SOL_ADDR_G5_02"),
            (WalletId3, "Ethereum", "ETH_ADDR_G5_03"),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "wallet",
                    id,
                    body = new
                    {
                        id,
                        avatar_id             = AvatarId1,
                        chain_type            = chain,
                        address               = addr,
                        public_key            = (string?)null,
                        label                 = $"G5 Wallet {id[^2..]}",
                        is_default            = false,
                        wallet_type           = "Platform",
                        encrypted_private_key = (string?)null,
                        encrypted_seed_phrase = (string?)null,
                        created_date          = DateTime.UtcNow,
                    },
                });
        }

        // ── bridge_tx ─────────────────────────────────────────────────────────
        // Each row must have a unique idempotency_key (UNIQUE index).
        // bridge_tx_lock_route UNIQUE on (source_chain, lock_tx_hash, target_chain):
        // lock_tx_hash is null here so no collision between pre-lock rows.
        foreach (var (id, idem) in new[]
        {
            (BridgeId1, "g5-idem-br-01"),
            (BridgeId2, "g5-idem-br-02"),
            (BridgeId3, "g5-idem-br-03"),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "bridge_tx",
                    id,
                    body = new
                    {
                        id,
                        avatar_id      = AvatarId1,
                        source_chain   = "Algorand",
                        target_chain   = "Solana",
                        source_token_id = "ASA:123",
                        target_token_id = (string?)null,
                        source_address = $"SRC_{id[^6..]}",
                        target_address = $"TGT_{id[^6..]}",
                        amount         = "1000",
                        status         = "Initiated",
                        mode           = "Trusted",
                        lock_tx_hash   = (string?)null,
                        mint_tx_hash   = (string?)null,
                        proof_data     = (string?)null,
                        error_message  = (string?)null,
                        created_at     = DateTime.UtcNow,
                        completed_at   = (DateTime?)null,
                        idempotency_key = idem,
                    },
                });
        }

        // ── nft_ownership ─────────────────────────────────────────────────────
        foreach (var (id, tokenId) in new[]
        {
            (NftId1, "TOKEN_G5_01"),
            (NftId2, "TOKEN_G5_02"),
            (NftId3, "TOKEN_G5_03"),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "nft_ownership",
                    id,
                    body = new
                    {
                        id,
                        avatar_id        = AvatarId1,
                        chain_type       = "Ethereum",
                        contract_address = "0xCONTRACT_G5",
                        token_id         = tokenId,
                        token_standard   = "ERC721",
                        metadata_uri     = $"ipfs://G5/{tokenId}",
                        image_uri        = (string?)null,
                        name             = $"G5 NFT {tokenId}",
                        description      = (string?)null,
                        attributes       = (object?)null,
                        royalty_percentage = 0.0m,
                        royalty_recipient = (string?)null,
                        is_soulbound     = false,
                        is_transferable  = true,
                        is_current       = true,
                        current_owner    = (string?)null,
                        is_active        = true,
                        minted_date      = DateTime.UtcNow,
                        last_transfer_date = (DateTime?)null,
                    },
                });
        }

        // ── operation_log ─────────────────────────────────────────────────────
        foreach (var (id, opType) in new[]
        {
            (OpId1, "Mint"),
            (OpId2, "Burn"),
            (OpId3, "Transfer"),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "operation_log",
                    id,
                    body = new
                    {
                        id,
                        operation_type = opType,
                        status         = "Pending",
                        created_date   = DateTime.UtcNow,
                    },
                });
        }

        // ── consumed_vaa_ledger ───────────────────────────────────────────────
        // Each row needs a unique (emitter_chain_id, emitter_address, sequence) triple.
        foreach (var (id, seq, digest) in new[]
        {
            (VaaId1, 1001L, MakeHex32("g5-vaa-01")),
            (VaaId2, 1002L, MakeHex32("g5-vaa-02")),
            (VaaId3, 1003L, MakeHex32("g5-vaa-03")),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "consumed_vaa_ledger",
                    id,
                    body = new
                    {
                        id,
                        digest,
                        emitter_chain_id       = 2,
                        emitter_address        = MakeHex64("g5-emit-01"),
                        sequence               = seq,
                        bridge_transaction_id  = BridgeId1,
                        consumed_at            = DateTime.UtcNow,
                    },
                });
        }

        // ── idempotency_key_store ─────────────────────────────────────────────
        // Each row must have a unique `key` (UNIQUE index).
        foreach (var (id, key) in new[]
        {
            (IdemId1, "g5-idem-key-01"),
            (IdemId2, "g5-idem-key-02"),
            (IdemId3, "g5-idem-key-03"),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "idempotency_key_store",
                    id,
                    body = new
                    {
                        id,
                        key,
                        operation_type  = "bridge_redeem",
                        state           = "Completed",
                        result_payload  = "{\"ok\":true}",
                        error           = (string?)null,
                        created_at      = DateTime.UtcNow,
                        updated_at      = DateTime.UtcNow,
                        ttl_expires_at  = (DateTime?)null,
                    },
                });
        }

        // ── saga_steps ────────────────────────────────────────────────────────
        foreach (var (id, step) in new[]
        {
            (SagaId1, "LockSource"),
            (SagaId2, "AwaitVAA"),
            (SagaId3, "Redeem"),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "saga_steps",
                    id,
                    body = new
                    {
                        id,
                        correlation_key      = $"corr-g5-{id[^6..]}",
                        saga_name            = "BridgeTransfer",
                        step_name            = step,
                        step_idempotency_key = $"saga-idem-g5-{id[^6..]}",
                        payload              = "{\"amount\":\"1.0\"}",
                        status               = "Pending",
                        is_compensation      = false,
                        attempt_count        = 0,
                        next_run_at          = DateTime.UtcNow,
                        claimed_at           = (DateTime?)null,
                        last_error           = (string?)null,
                        output               = (string?)null,
                        dead_lettered        = false,
                        created_at           = DateTime.UtcNow,
                        updated_at           = DateTime.UtcNow,
                    },
                });
        }

        // ── avatar ────────────────────────────────────────────────────────────
        // Each row must have a unique username and email (UNIQUE indexes).
        foreach (var (id, username, email) in new[]
        {
            (AvatarId1, "g5_user_01", "g5_01@g5test.internal"),
            (AvatarId2, "g5_user_02", "g5_02@g5test.internal"),
            (AvatarId3, "g5_user_03", "g5_03@g5test.internal"),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "avatar",
                    id,
                    body = new
                    {
                        id,
                        username      = username,
                        email,
                        password_hash = "hashed_g5_secret",
                        title         = (string?)null,
                        first_name    = "G5",
                        last_name     = $"User_{id[^2..]}",
                        created_date  = DateTime.UtcNow,
                        last_beamed_in_date = (DateTime?)null,
                        is_active     = true,
                        is_verified   = false,
                        karma         = 0,
                        level         = 1,
                    },
                });
        }

        // ── holon ─────────────────────────────────────────────────────────────
        foreach (var (id, name) in new[]
        {
            (HolonId1, "G5 Holon Alpha"),
            (HolonId2, "G5 Holon Beta"),
            (HolonId3, "G5 Holon Gamma"),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "holon",
                    id,
                    body = new
                    {
                        id,
                        name,
                        description      = $"G5 test holon {id[^2..]}",
                        parent_holon_id  = (string?)null,
                        avatar_id        = AvatarId1,
                        provider_name    = "SurrealDB",
                        chain_id         = (string?)null,
                        asset_type       = (string?)null,
                        token_id         = (string?)null,
                        metadata         = (object?)null,
                        peer_holon_ids   = (string[]?)null,
                        created_date     = DateTime.UtcNow,
                        modified_date    = (DateTime?)null,
                        is_active        = true,
                    },
                });
        }

        // ── star_odk ─────────────────────────────────────────────────────────
        // Note: the table name is star_odk (not star) per Persistence/SurrealDb/Schemas/110_star.surql.
        foreach (var (id, starName) in new[]
        {
            (StarId1, "G5 Star Alpha"),
            (StarId2, "G5 Star Beta"),
            (StarId3, "G5 Star Gamma"),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "star_odk",
                    id,
                    body = new
                    {
                        id,
                        name              = starName,
                        description       = $"G5 test star {id[^2..]}",
                        public_key        = (string?)null,
                        private_key_hash  = (string?)null,
                        avatar_id         = AvatarId1,
                        bound_holon_ids   = (string[]?)null,
                        target_chain      = "Solana",
                        generated_code    = (string?)null,
                        deployment_config = (string?)null,
                        created_date      = DateTime.UtcNow,
                        modified_date     = (DateTime?)null,
                        is_active         = true,
                    },
                });
        }

        // ── api_key ───────────────────────────────────────────────────────────
        // Each row must have a unique key_hash (UNIQUE index).
        foreach (var (id, hash, prefix) in new[]
        {
            (ApiKeyId1, "g5-hash-ak-01", "azoa_g501"),
            (ApiKeyId2, "g5-hash-ak-02", "azoa_g502"),
            (ApiKeyId3, "g5-hash-ak-03", "azoa_g503"),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "api_key",
                    id,
                    body = new
                    {
                        id,
                        avatar_id    = AvatarId1,
                        name         = $"G5 API Key {id[^2..]}",
                        key_hash     = hash,
                        key_prefix   = prefix,
                        created_date = DateTime.UtcNow,
                        expires_at   = (DateTime?)null,
                        last_used_at = (DateTime?)null,
                        revoked_at   = (DateTime?)null,
                        is_active    = true,
                        scopes       = "read,write",
                    },
                });
        }

        // ── quest_template ────────────────────────────────────────────────────
        foreach (var (id, name) in new[]
        {
            (QuestTplId1, "G5 Quest Alpha"),
            (QuestTplId2, "G5 Quest Beta"),
            (QuestTplId3, "G5 Quest Gamma"),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "quest_template",
                    id,
                    body = new
                    {
                        id,
                        name,
                        description      = $"G5 quest template {id[^2..]}",
                        author_avatar_id = AvatarId1,
                        parameters       = "{}",
                        version          = "1.0.0",
                        is_public        = false,
                        nodes            = Array.Empty<object>(),
                        edges            = Array.Empty<object>(),
                        tags             = Array.Empty<string>(),
                    },
                });
        }

        // ── quest_node_template ───────────────────────────────────────────────
        foreach (var (id, name) in new[]
        {
            (QuestNodeTplId1, "G5 Node Alpha"),
            (QuestNodeTplId2, "G5 Node Beta"),
            (QuestNodeTplId3, "G5 Node Gamma"),
        })
        {
            await ExecuteSurrealSqlAsync(
                "CREATE type::record($t, $id) CONTENT $body RETURN AFTER",
                new
                {
                    t = "quest_node_template",
                    id,
                    body = new
                    {
                        id,
                        name,
                        node_type        = "HolonCreate",
                        description      = $"G5 node template {id[^2..]}",
                        default_config   = "{}",
                        config_schema    = "{}",
                        input_schema     = "{}",
                        output_schema    = "{}",
                        version          = "1.0.0",
                        author_avatar_id = AvatarId1,
                        is_public        = false,
                        tags             = Array.Empty<string>(),
                    },
                });
        }
    }

    // ── Checksum helper ───────────────────────────────────────────────────────

    /// <summary>
    /// Computes a deterministic SHA-256 hex checksum over a table's rows.
    /// Sort order: rows are sorted by their "id" property (string sort).
    /// For each row, produces a canonical JSON string with properties sorted
    /// alphabetically by name, then concatenates all rows and SHA-256s the result.
    /// </summary>
    private static string ComputeTableChecksum(IEnumerable<JsonElement> rows)
    {
        // Sort rows by id field (string comparison).
        var sorted = rows
            .OrderBy(r => r.TryGetProperty("id", out var idProp) ? idProp.ToString() : string.Empty,
                StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        foreach (var row in sorted)
        {
            // Produce canonical JSON: sorted property names, deterministic values.
            var canonicalJson = SerializeWithSortedKeys(row);
            sb.Append(canonicalJson);
        }

        var bytes  = Encoding.UTF8.GetBytes(sb.ToString());
        var hash   = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Recursively re-serialises a <see cref="JsonElement"/> with properties
    /// sorted alphabetically by key name. This produces a canonical JSON
    /// representation regardless of the order SurrealDB returns properties.
    /// </summary>
    private static string SerializeWithSortedKeys(JsonElement element)
    {
        using var ms      = new System.IO.MemoryStream();
        using var writer  = new Utf8JsonWriter(ms);
        WriteElementSorted(writer, element);
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteElementSorted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Sort properties alphabetically so the output is deterministic
                // regardless of the order SurrealDB returns them.
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteElementSorted(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteElementSorted(writer, item);
                writer.WriteEndArray();
                break;

            default:
                // Scalars, nulls, booleans, numbers — write verbatim.
                element.WriteTo(writer);
                break;
        }
    }

    // ── Infrastructure helpers ────────────────────────────────────────────────

    /// <summary>
    /// Lists all value tables covered by G5.
    /// Note: star_odk is the SurrealDB table name for the star entity.
    /// </summary>
    private static string[] AllValueTables() =>
    [
        "wallet",
        "bridge_tx",
        "nft_ownership",
        "operation_log",
        "consumed_vaa_ledger",
        "idempotency_key_store",
        "saga_steps",
        "avatar",
        "holon",
        "star_odk",
        "api_key",
        "quest_template",
        "quest_node_template",
    ];

    /// <summary>
    /// Reads all rows from a SurrealDB table via the direct HTTP API,
    /// returning each row as a <see cref="JsonElement"/> for checksum computation.
    /// </summary>
    private async Task<IEnumerable<JsonElement>> SelectAllRowsAsync(string table)
    {
        // Use parameterised query: table name cannot be a parameter in SurrealQL
        // SELECT, but it is a compile-time constant in this test (G3-safe: the
        // values come from AllValueTables(), not user input).
        // We build the SQL with the table name embedded because SurrealQL does not
        // support $param in the FROM clause.
        var sql = $"SELECT * FROM {table}";

        var result = new List<JsonElement>();
        if (!await SkipIfSurrealDbUnavailableAsync()) return result;

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));

        using var http = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        http.DefaultRequestHeaders.Add("NS", TestNamespace);
        http.DefaultRequestHeaders.Add("DB", "test");
        http.DefaultRequestHeaders.Add("Accept", "application/json");

        var content  = new StringContent(sql, Encoding.UTF8, "text/plain");
        var response = await http.PostAsync("/sql", content);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // SurrealDB HTTP /sql returns an array of statement results.
        // Each element has {"status":"OK","result":[...rows...]}.
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var stmtResult in root.EnumerateArray())
            {
                if (stmtResult.TryGetProperty("result", out var rows) &&
                    rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                        result.Add(row.Clone()); // clone to outlive the document
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Applies all *.surql schema files from Persistence/SurrealDb/Schemas/
    /// to the test namespace so that SCHEMAFULL tables accept the seed rows.
    /// </summary>
    private async Task ApplyAllSchemasAsync(string repoRoot)
    {
        var schemaDir = Path.Combine(repoRoot, "Persistence", "SurrealDb", "Schemas");
        if (!Directory.Exists(schemaDir)) return;

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));

        foreach (var file in Directory.GetFiles(schemaDir, "*.surql").OrderBy(f => f))
        {
            var ddl = await File.ReadAllTextAsync(file);

            using var ddlClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
            ddlClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
            ddlClient.DefaultRequestHeaders.Add("NS", TestNamespace);
            ddlClient.DefaultRequestHeaders.Add("DB", "test");

            var content  = new StringContent(ddl, Encoding.UTF8, "text/plain");
            var response = await ddlClient.PostAsync("/sql", content);
            _ = response; // best-effort; schema may already be defined
        }
    }

    /// <summary>
    /// Runs a pwsh command and returns (exitCode, stdout, stderr).
    /// Uses pwsh.exe (PowerShell 7) on Windows; falls back to pwsh on Unix.
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunPwsh(string arguments)
    {
        var exeName = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";

        var psi = new ProcessStartInfo
        {
            FileName               = exeName,
            Arguments              = arguments,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {exeName}");

        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdOut, stdErr);
    }

    /// <summary>
    /// Locates the repository root by walking up from
    /// <see cref="AppContext.BaseDirectory"/> until AZOA.WebAPI.csproj is found.
    /// Mirrors <c>IntegrationTestBase.FindRepoRoot()</c> which is private there.
    /// </summary>
    private static string? FindRepoRootLocal()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AZOA.WebAPI.csproj")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Produces a deterministic 64-char lowercase hex string (SHA-256) from a seed.
    /// Used for emitter addresses and similar fields that must be exactly 64 hex chars.
    /// </summary>
    private static string MakeHex64(string seed)
    {
        var data = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(data).ToLowerInvariant(); // SHA256 = 32 bytes = 64 hex chars
    }

    /// <summary>
    /// Produces a deterministic 64-char lowercase hex string (SHA-256) for digest fields.
    /// VAA digest fields in consumed_vaa_ledger are conventionally 64-char hex.
    /// </summary>
    private static string MakeHex32(string seed)
    {
        var data = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(data).ToLowerInvariant();
    }
}
