using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OASIS.WebAPI.Models;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var dictConverter = new ValueConverter<Dictionary<string, string>, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonOptions) ?? new Dictionary<string, string>());

        var listGuidConverter = new ValueConverter<List<Guid>, string>(
            v => JsonSerializer.Serialize(v, JsonOptions),
            v => JsonSerializer.Deserialize<List<Guid>>(v, JsonOptions) ?? new List<Guid>());

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

        base.OnModelCreating(modelBuilder);
    }
}
