using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace OASIS.WebAPI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNFTSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create converter for dictionary serialization
            var dictConverter = new ValueConverter<Dictionary<string, string>, string>(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new Dictionary<string, string>());

            migrationBuilder.CreateTable(
                name: "AvatarNFTs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AvatarId = table.Column<Guid>(type: "uuid", nullable: false),
                    NFTContractAddress = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    TokenId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ChainType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenStandard = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MetadataURI = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ImageURI = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Attributes = table.Column<string>(type: "text", nullable: false, converter: dictConverter),
                    RoyaltyPercentage = table.Column<decimal>(type: "numeric", nullable: false),
                    RoyaltyRecipient = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsSoulbound = table.Column<bool>(type: "boolean", nullable: false),
                    IsTransferable = table.Column<bool>(type: "boolean", nullable: false),
                    MintedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastTransferDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CurrentOwner = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvatarNFTs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AvatarNFTs_Avatars_AvatarId",
                        column: x => x.AvatarId,
                        principalTable: "Avatars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HolonNFTBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HolonId = table.Column<Guid>(type: "uuid", nullable: false),
                    AvatarNFTId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PermissionLevel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Permissions = table.Column<string>(type: "text", nullable: false, converter: dictConverter),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HolonNFTBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HolonNFTBindings_AvatarNFTs_AvatarNFTId",
                        column: x => x.AvatarNFTId,
                        principalTable: "AvatarNFTs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HolonNFTBindings_Holons_HolonId",
                        column: x => x.HolonId,
                        principalTable: "Holons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WalletNFTBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    AvatarNFTId = table.Column<Guid>(type: "uuid", nullable: false),
                    BindingType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccessLevel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AccessPermissions = table.Column<string>(type: "text", nullable: false, converter: dictConverter),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletNFTBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalletNFTBindings_AvatarNFTs_AvatarNFTId",
                        column: x => x.AvatarNFTId,
                        principalTable: "AvatarNFTs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WalletNFTBindings_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AvatarNFTs_AvatarId",
                table: "AvatarNFTs",
                column: "AvatarId");

            migrationBuilder.CreateIndex(
                name: "IX_AvatarNFTs_ChainType",
                table: "AvatarNFTs",
                column: "ChainType");

            migrationBuilder.CreateIndex(
                name: "IX_AvatarNFTs_CurrentOwner",
                table: "AvatarNFTs",
                column: "CurrentOwner");

            migrationBuilder.CreateIndex(
                name: "IX_AvatarNFTs_NFTContractAddress",
                table: "AvatarNFTs",
                column: "NFTContractAddress");

            migrationBuilder.CreateIndex(
                name: "IX_AvatarNFTs_TokenId",
                table: "AvatarNFTs",
                column: "TokenId");

            migrationBuilder.CreateIndex(
                name: "IX_HolonNFTBindings_AvatarNFTId",
                table: "HolonNFTBindings",
                column: "AvatarNFTId");

            migrationBuilder.CreateIndex(
                name: "IX_HolonNFTBindings_HolonId",
                table: "HolonNFTBindings",
                column: "HolonId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletNFTBindings_AvatarNFTId",
                table: "WalletNFTBindings",
                column: "AvatarNFTId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletNFTBindings_WalletId",
                table: "WalletNFTBindings",
                column: "WalletId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "WalletNFTBindings");
            migrationBuilder.DropTable(name: "HolonNFTBindings");
            migrationBuilder.DropTable(name: "AvatarNFTs");
        }
    }
}