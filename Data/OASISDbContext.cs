using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Idempotency;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Responses;
using OASIS.WebAPI.Models.Sagas;

namespace OASIS.WebAPI.Data;

public class OASISDbContext : DbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public OASISDbContext(DbContextOptions<OASISDbContext> options) : base(options) { }

    public DbSet<Avatar> Avatars => Set<Avatar>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Holon> Holons => Set<Holon>();
    public DbSet<BlockchainOperation> BlockchainOperations => Set<BlockchainOperation>();
    public DbSet<STARODK> STARODKs => Set<STARODK>();
    public DbSet<AvatarNFT> AvatarNFTs => Set<AvatarNFT>();
    public DbSet<HolonNFTBinding> HolonNFTBindings => Set<HolonNFTBinding>();
    public DbSet<WalletNFTBinding> WalletNFTBindings => Set<WalletNFTBinding>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<BridgeTransactionResult> BridgeTransactions => Set<BridgeTransactionResult>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<ConsumedVaaRecord> ConsumedVaas => Set<ConsumedVaaRecord>();

    // Durable saga / transactional outbox (durable-saga-orchestration Phase 1).
    // Generic — no bridge coupling; the bridge becomes one consumer later.
    public DbSet<SagaStepRecord> SagaSteps => Set<SagaStepRecord>();

    // Quest entities
    public DbSet<Quest> Quests => Set<Quest>();
    public DbSet<QuestNode> QuestNodes => Set<QuestNode>();
    public DbSet<QuestEdge> QuestEdges => Set<QuestEdge>();
    public DbSet<QuestDependency> QuestDependencies => Set<QuestDependency>();
    public DbSet<QuestNodeTemplate> QuestNodeTemplates => Set<QuestNodeTemplate>();
    public DbSet<QuestTemplate> QuestTemplates => Set<QuestTemplate>();
    public DbSet<QuestTemplateNode> QuestTemplateNodes => Set<QuestTemplateNode>();
    public DbSet<QuestTemplateEdge> QuestTemplateEdges => Set<QuestTemplateEdge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var dictConverter = new ValueConverter<Dictionary<string, string>, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonOptions) ?? new Dictionary<string, string>());

        var listGuidConverter = new ValueConverter<List<Guid>, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<List<Guid>>(v, JsonOptions) ?? new List<Guid>());

        var listStringConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<List<string>>(v, JsonOptions) ?? new List<string>());

        modelBuilder.Entity<Avatar>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.Email).HasMaxLength(256);
            entity.Property(e => e.Username).HasMaxLength(128);
            entity.Property(e => e.PasswordHash).HasMaxLength(256);
            entity.Property(e => e.Title).HasMaxLength(64);
            entity.Property(e => e.FirstName).HasMaxLength(128);
            entity.Property(e => e.LastName).HasMaxLength(128);

            entity.HasMany(e => e.Wallets)
                  .WithOne()
                  .HasForeignKey(e => e.AvatarId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Wallet>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AvatarId);
            entity.HasIndex(e => e.Address);
            entity.Property(e => e.Address).HasMaxLength(512);
            entity.Property(e => e.ChainType).HasMaxLength(64);
            entity.Property(e => e.PublicKey).HasMaxLength(512);
            entity.Property(e => e.Label).HasMaxLength(128);
            entity.Property(e => e.WalletType)
                  .HasConversion<int>();
            entity.Property(e => e.EncryptedPrivateKey).HasMaxLength(1024);
            entity.Property(e => e.EncryptedSeedPhrase).HasMaxLength(1024);
        });

        modelBuilder.Entity<Holon>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AvatarId);
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.ProviderName);
            entity.HasIndex(e => e.ChainId);
            entity.Property(e => e.Name).HasMaxLength(256);
            entity.Property(e => e.Description).HasMaxLength(2048);
            entity.Property(e => e.ProviderName).HasMaxLength(64);
            entity.Property(e => e.ChainId).HasMaxLength(64);
            entity.Property(e => e.AssetType).HasMaxLength(64);
            entity.Property(e => e.TokenId).HasMaxLength(256);

            entity.HasOne(e => e.ParentHolon)
                  .WithMany(e => e.SubHolons)
                  .HasForeignKey(e => e.ParentHolonId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.Property(e => e.Metadata).HasConversion(dictConverter);
            entity.Property(e => e.PeerHolonIds).HasConversion(listGuidConverter);
        });

        modelBuilder.Entity<BlockchainOperation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AvatarId);
            entity.HasIndex(e => e.WalletId);
            entity.HasIndex(e => e.OperationType);
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.OperationType).HasMaxLength(64);
            entity.Property(e => e.Status).HasMaxLength(64);
            entity.Property(e => e.TokenUri).HasMaxLength(1024);
            entity.Property(e => e.AssetType).HasMaxLength(64);
            entity.Property(e => e.ExchangeRate).HasMaxLength(64);
            entity.Property(e => e.RecipientAddress).HasMaxLength(512);

            entity.Property(e => e.Parameters).HasConversion(dictConverter);
        });

        modelBuilder.Entity<STARODK>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.AvatarId);
            entity.Property(e => e.Name).HasMaxLength(256);
            entity.Property(e => e.Description).HasMaxLength(1024);
            entity.Property(e => e.PublicKey).HasMaxLength(512);
            entity.Property(e => e.PrivateKeyHash).HasMaxLength(512);
            entity.Property(e => e.TargetChain).HasMaxLength(64);

            entity.Property(e => e.BoundHolonIds).HasConversion(listGuidConverter);

            entity.HasOne<Avatar>()
                  .WithMany()
                  .HasForeignKey(e => e.AvatarId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AvatarNFT>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AvatarId);
            entity.HasIndex(e => e.NFTContractAddress);
            entity.HasIndex(e => e.TokenId);
            entity.HasIndex(e => e.ChainType);
            entity.HasIndex(e => e.CurrentOwner);
            entity.Property(e => e.NFTContractAddress).HasMaxLength(512);
            entity.Property(e => e.TokenId).HasMaxLength(256);
            entity.Property(e => e.ChainType).HasMaxLength(64);
            entity.Property(e => e.TokenStandard).HasMaxLength(64);
            entity.Property(e => e.MetadataURI).HasMaxLength(1024);
            entity.Property(e => e.ImageURI).HasMaxLength(1024);
            entity.Property(e => e.Name).HasMaxLength(256);
            entity.Property(e => e.Description).HasMaxLength(1024);
            entity.Property(e => e.CurrentOwner).HasMaxLength(512);
            entity.Property(e => e.RoyaltyRecipient).HasMaxLength(512);

            entity.Property(e => e.Attributes).HasConversion(dictConverter);
        });

        modelBuilder.Entity<HolonNFTBinding>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.HolonId);
            entity.HasIndex(e => e.AvatarNFTId);
            entity.Property(e => e.Role).HasMaxLength(64);
            entity.Property(e => e.PermissionLevel).HasMaxLength(64);

            entity.Property(e => e.Permissions).HasConversion(dictConverter);
        });

        modelBuilder.Entity<WalletNFTBinding>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WalletId);
            entity.HasIndex(e => e.AvatarNFTId);
            entity.Property(e => e.BindingType).HasMaxLength(64);
            entity.Property(e => e.AccessLevel).HasMaxLength(64);

            entity.Property(e => e.AccessPermissions).HasConversion(dictConverter);
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.HasIndex(e => e.AvatarId);
            entity.HasIndex(e => e.KeyPrefix);
            entity.Property(e => e.Name).HasMaxLength(128);
            entity.Property(e => e.KeyHash).HasMaxLength(128);
            entity.Property(e => e.KeyPrefix).HasMaxLength(16);
            entity.Property(e => e.Scopes).HasMaxLength(512);

            entity.HasOne<Avatar>()
                  .WithMany()
                  .HasForeignKey(e => e.AvatarId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BridgeTransactionResult>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AvatarId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.SourceChain, e.TargetChain });
            entity.Property(e => e.IdempotencyKey).HasMaxLength(200);
            entity.HasIndex(e => e.IdempotencyKey);

            // Source dedupe: a given on-chain lock tx may back at most one
            // bridge row. Filtered to non-null so legacy/pre-lock rows coexist.
            entity.HasIndex(e => e.LockTxHash)
                  .IsUnique()
                  .HasFilter("\"LockTxHash\" IS NOT NULL");

            // Wormhole dedupe: a given (emitterChain, emitterAddress, sequence)
            // identifies exactly one cross-chain message ⇒ one bridge row.
            entity.HasIndex(e => new { e.WormholeEmitterChainId, e.WormholeEmitterAddress, e.WormholeSequence })
                  .IsUnique()
                  .HasFilter("\"WormholeEmitterChainId\" IS NOT NULL AND \"WormholeEmitterAddress\" IS NOT NULL AND \"WormholeSequence\" IS NOT NULL");
        });

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.HasKey(e => e.Key);
            // Uniqueness on Key is the atomicity primitive: insert-wins.
            entity.HasIndex(e => e.Key).IsUnique();
            entity.Property(e => e.Key).HasMaxLength(200);
            entity.Property(e => e.OperationType).HasMaxLength(64);
            entity.Property(e => e.State).HasConversion<int>();
            entity.Property(e => e.ResultPayload).HasMaxLength(4096);
            entity.Property(e => e.Error).HasMaxLength(1024);
            entity.HasIndex(e => e.OperationType);
        });

        modelBuilder.Entity<ConsumedVaaRecord>(entity =>
        {
            entity.HasKey(e => e.Digest);
            // Uniqueness on Digest is the VAA replay-protection primitive.
            entity.HasIndex(e => e.Digest).IsUnique();
            entity.Property(e => e.Digest).HasMaxLength(128);
            entity.Property(e => e.EmitterAddress).HasMaxLength(128);
            entity.Property(e => e.BridgeTransactionId).HasMaxLength(64);
            entity.HasIndex(e => new { e.EmitterChainId, e.EmitterAddress, e.Sequence });
        });

        modelBuilder.Entity<SagaStepRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CorrelationKey).HasMaxLength(200);
            entity.Property(e => e.SagaName).HasMaxLength(128);
            entity.Property(e => e.StepName).HasMaxLength(128);
            entity.Property(e => e.StepIdempotencyKey).HasMaxLength(200);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.LastError).HasMaxLength(2048);
            entity.Property(e => e.Output).HasMaxLength(4096);

            // NON-unique: the outbox legitimately holds many rows per
            // correlation (forward steps + compensation + retries). Exactly-once
            // is enforced by handlers via IIdempotencyStore, NOT a constraint here.
            entity.HasIndex(e => e.CorrelationKey);
            entity.HasIndex(e => e.SagaName);
            // Drives the "what is due?" scan: claimable rows are
            // Status==Pending AND NextRunAt<=now.
            entity.HasIndex(e => new { e.Status, e.NextRunAt });
            entity.HasIndex(e => e.DeadLettered);
        });

        // Quest configurations. Runtime fields (Status/CompletedDate on Quest;
        // State/Output/Error on QuestNode) moved to QuestRun + QuestNodeExecution
        // by the quest-temporal-fork-model track (see ADR §2.2).
        modelBuilder.Entity<Quest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AvatarId);
            entity.HasIndex(e => e.TemplateId);
            entity.HasIndex(e => e.DappSeriesId);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Metadata).HasConversion(dictConverter);
        });

        modelBuilder.Entity<QuestNode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.QuestId);
            entity.HasIndex(e => e.NodeType);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Config).HasColumnType("text");
        });

        modelBuilder.Entity<QuestEdge>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.QuestId);
            entity.HasIndex(e => new { e.SourceNodeId, e.TargetNodeId }).IsUnique();
            entity.Property(e => e.Condition).HasColumnType("text");
        });

        modelBuilder.Entity<QuestDependency>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.QuestId);
            entity.HasIndex(e => e.DependsOnQuestId);
        });

        modelBuilder.Entity<QuestNodeTemplate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.IsPublic);
            entity.HasIndex(e => e.NodeType);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.DefaultConfig).HasColumnType("text");
            entity.Property(e => e.ConfigSchema).HasColumnType("text");
            entity.Property(e => e.InputSchema).HasColumnType("text");
            entity.Property(e => e.OutputSchema).HasColumnType("text");
            entity.Property(e => e.Tags).HasConversion(listStringConverter);
        });

        modelBuilder.Entity<QuestTemplate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.IsPublic);
            entity.HasIndex(e => e.AuthorAvatarId);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Parameters).HasColumnType("text");
            entity.Property(e => e.Tags).HasConversion(listStringConverter);
        });

        modelBuilder.Entity<QuestTemplateNode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TemplateId);
            entity.HasIndex(e => e.NodeTemplateId);
            entity.HasIndex(e => e.SlotId);
            entity.Property(e => e.SlotId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ParamOverrides).HasColumnType("text");
        });

        modelBuilder.Entity<QuestTemplateEdge>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TemplateId);
            entity.Property(e => e.SourceSlotId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TargetSlotId).HasMaxLength(200).IsRequired();
        });

        base.OnModelCreating(modelBuilder);
    }
}
