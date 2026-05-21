using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Quest;

namespace OASIS.WebAPI.Data;

/// <summary>
/// Seeds the database with demo data for functional testing.
/// Only runs if the database is empty (no avatars exist).
/// </summary>
public static class SeedData
{
    public static async Task SeedAsync(OASISDbContext db)
    {
        if (db.Avatars.Any()) return; // Already seeded

        // ─── Demo Avatar ───
        var avatarId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var avatar = new Avatar
        {
            Id = avatarId,
            Username = "demo",
            Email = "demo@oasis.local",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("demo123"),
            FirstName = "Demo",
            LastName = "User",
            Title = "OASIS Explorer",
            IsActive = true,
        };
        db.Avatars.Add(avatar);

        // ─── Wallets ───
        var algoWalletId = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var solWalletId = Guid.Parse("00000000-0000-0000-0000-000000000011");

        db.Wallets.Add(new Wallet
        {
            Id = algoWalletId,
            AvatarId = avatarId,
            ChainType = "Algorand",
            Address = "7J6ZZGF2UPNKKBCJA4DHFKVL6LXGKKDQM6KX4YZ5J5H5F7ZJGX6W4PUJJY",
            Label = "Demo Algorand Wallet",
            IsDefault = true,
        });
        db.Wallets.Add(new Wallet
        {
            Id = solWalletId,
            AvatarId = avatarId,
            ChainType = "Solana",
            Address = "So11111111111111111111111111111111111111112",
            Label = "Demo Solana Wallet",
            IsDefault = true,
        });

        // ─── Holons ───
        var rootHolonId = Guid.Parse("00000000-0000-0000-0000-000000000020");
        var childHolon1Id = Guid.Parse("00000000-0000-0000-0000-000000000021");
        var childHolon2Id = Guid.Parse("00000000-0000-0000-0000-000000000022");
        var nftHolonId = Guid.Parse("00000000-0000-0000-0000-000000000023");

        db.Holons.Add(new Holon
        {
            Id = rootHolonId,
            Name = "OASIS Root",
            Description = "Root holon of the demo holarchy",
            AvatarId = avatarId,
            ProviderName = "PostgreSQL",
            IsActive = true,
            Metadata = new Dictionary<string, string> { ["type"] = "root", ["version"] = "1.0" },
        });
        db.Holons.Add(new Holon
        {
            Id = childHolon1Id,
            Name = "Algorand Zone",
            Description = "Holons related to Algorand blockchain",
            AvatarId = avatarId,
            ParentHolonId = rootHolonId,
            ProviderName = "PostgreSQL",
            ChainId = "algorand",
            IsActive = true,
        });
        db.Holons.Add(new Holon
        {
            Id = childHolon2Id,
            Name = "Solana Zone",
            Description = "Holons related to Solana blockchain",
            AvatarId = avatarId,
            ParentHolonId = rootHolonId,
            ProviderName = "PostgreSQL",
            ChainId = "solana",
            IsActive = true,
        });
        db.Holons.Add(new Holon
        {
            Id = nftHolonId,
            Name = "Demo NFT",
            Description = "A sample NFT holon for testing",
            AvatarId = avatarId,
            ParentHolonId = childHolon1Id,
            ProviderName = "PostgreSQL",
            ChainId = "algorand",
            AssetType = "NFT",
            TokenId = "demo-token-001",
            IsActive = true,
            Metadata = new Dictionary<string, string>
            {
                ["image"] = "https://via.placeholder.com/300",
                ["artist"] = "OASIS Demo",
            },
        });

        // ─── STAR ODK ───
        db.STARODKs.Add(new STARODK
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000030"),
            Name = "Demo dApp",
            Description = "A sample STAR ODK for testing the dApp generation pipeline",
            AvatarId = avatarId,
            TargetChain = "Algorand",
            IsActive = true,
        });

        // ─── Quest (simple 2-node DAG) ───
        var questId = Guid.Parse("00000000-0000-0000-0000-000000000040");
        var node1Id = Guid.Parse("00000000-0000-0000-0000-000000000041");
        var node2Id = Guid.Parse("00000000-0000-0000-0000-000000000042");

        db.Quests.Add(new Quest
        {
            Id = questId,
            Name = "Demo Quest: Create & Query Holon",
            Description = "A simple 2-step quest that creates a holon then queries it",
            AvatarId = avatarId,
            // Status moved to QuestRun (see quest-temporal-fork-model ADR §2.2).
            Nodes = new List<QuestNode>
            {
                new()
                {
                    Id = node1Id,
                    QuestId = questId,
                    Name = "Create Test Holon",
                    NodeType = QuestNodeType.HolonCreate,
                    Config = "{\"name\":\"Quest-Created Holon\",\"description\":\"Created by quest execution\"}",
                    IsEntry = true,
                    IsTerminal = false,
                    ExecutionOrder = 0,
                },
                new()
                {
                    Id = node2Id,
                    QuestId = questId,
                    Name = "Query All Holons",
                    NodeType = QuestNodeType.HolonQuery,
                    Config = "{}",
                    IsEntry = false,
                    IsTerminal = true,
                    ExecutionOrder = 1,
                },
            },
            Edges = new List<QuestEdge>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    QuestId = questId,
                    SourceNodeId = node1Id,
                    TargetNodeId = node2Id,
                    EdgeType = QuestEdgeType.Control,
                },
            },
        });

        // ─── Quest Node Template ───
        db.QuestNodeTemplates.Add(new QuestNodeTemplate
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000050"),
            Name = "Create Holon Step",
            NodeType = QuestNodeType.HolonCreate,
            Description = "Reusable template for creating a holon with configurable name/description",
            DefaultConfig = "{\"name\":\"New Holon\",\"description\":\"Created from template\"}",
            ConfigSchema = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"}}}",
            InputSchema = "{}",
            OutputSchema = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}",
            Version = "1.0.0",
            AuthorAvatarId = avatarId,
            IsPublic = true,
            Tags = new List<string> { "holon", "create", "basic" },
        });

        await db.SaveChangesAsync();
    }
}
