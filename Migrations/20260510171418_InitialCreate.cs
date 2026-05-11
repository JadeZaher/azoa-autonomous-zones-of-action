using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OASIS.WebAPI.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Avatars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FirstName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastBeamedInDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    Karma = table.Column<int>(type: "integer", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Avatars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BlockchainOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AvatarId = table.Column<Guid>(type: "uuid", nullable: true),
                    WalletId = table.Column<Guid>(type: "uuid", nullable: true),
                    OperationType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Parameters = table.Column<string>(type: "text", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TokenUri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    AssetType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SourceHolonId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetHolonId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExchangeRate = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RecipientAddress = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockchainOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Holons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ParentHolonId = table.Column<Guid>(type: "uuid", nullable: true),
                    AvatarId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChainId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AssetType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TokenId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Metadata = table.Column<string>(type: "text", nullable: false),
                    PeerHolonIds = table.Column<string>(type: "text", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Holons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Holons_Holons_ParentHolonId",
                        column: x => x.ParentHolonId,
                        principalTable: "Holons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "QuestNodeTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NodeType = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DefaultConfig = table.Column<string>(type: "text", nullable: false),
                    ConfigSchema = table.Column<string>(type: "text", nullable: false),
                    InputSchema = table.Column<string>(type: "text", nullable: false),
                    OutputSchema = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    AuthorAvatarId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    Tags = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestNodeTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Quests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AvatarId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    DappSeriesId = table.Column<Guid>(type: "uuid", nullable: true),
                    Metadata = table.Column<string>(type: "text", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuestTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AuthorAvatarId = table.Column<Guid>(type: "uuid", nullable: false),
                    Parameters = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    Tags = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestTemplates", x => x.Id);
                });

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
                    Attributes = table.Column<string>(type: "text", nullable: false),
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
                name: "STARODKs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    PublicKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PrivateKeyHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    AvatarId = table.Column<Guid>(type: "uuid", nullable: true),
                    BoundHolonIds = table.Column<string>(type: "text", nullable: false),
                    TargetChain = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GeneratedCode = table.Column<string>(type: "text", nullable: true),
                    DeploymentConfig = table.Column<string>(type: "text", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_STARODKs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_STARODKs_Avatars_AvatarId",
                        column: x => x.AvatarId,
                        principalTable: "Avatars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Wallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AvatarId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChainType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Address = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    PublicKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wallets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Wallets_Avatars_AvatarId",
                        column: x => x.AvatarId,
                        principalTable: "Avatars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuestDependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    DependsOnQuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    DependsOnNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    DependencyType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestDependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuestDependencies_Quests_QuestId",
                        column: x => x.QuestId,
                        principalTable: "Quests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuestEdges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Condition = table.Column<string>(type: "text", nullable: true),
                    EdgeType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestEdges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuestEdges_Quests_QuestId",
                        column: x => x.QuestId,
                        principalTable: "Quests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuestNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeTemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    NodeType = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Config = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Output = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsEntry = table.Column<bool>(type: "boolean", nullable: false),
                    IsTerminal = table.Column<bool>(type: "boolean", nullable: false),
                    ExecutionOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuestNodes_Quests_QuestId",
                        column: x => x.QuestId,
                        principalTable: "Quests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuestTemplateEdges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSlotId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetSlotId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EdgeType = table.Column<int>(type: "integer", nullable: false),
                    QuestTemplateId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestTemplateEdges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuestTemplateEdges_QuestTemplates_QuestTemplateId",
                        column: x => x.QuestTemplateId,
                        principalTable: "QuestTemplates",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "QuestTemplateNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    SlotId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NodeTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParamOverrides = table.Column<string>(type: "text", nullable: false),
                    IsEntry = table.Column<bool>(type: "boolean", nullable: false),
                    IsTerminal = table.Column<bool>(type: "boolean", nullable: false),
                    QuestTemplateId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestTemplateNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuestTemplateNodes_QuestTemplates_QuestTemplateId",
                        column: x => x.QuestTemplateId,
                        principalTable: "QuestTemplates",
                        principalColumn: "Id");
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
                    Permissions = table.Column<string>(type: "text", nullable: false),
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
                    AccessPermissions = table.Column<string>(type: "text", nullable: false),
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
                name: "IX_Avatars_Email",
                table: "Avatars",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Avatars_Username",
                table: "Avatars",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BlockchainOperations_AvatarId",
                table: "BlockchainOperations",
                column: "AvatarId");

            migrationBuilder.CreateIndex(
                name: "IX_BlockchainOperations_OperationType",
                table: "BlockchainOperations",
                column: "OperationType");

            migrationBuilder.CreateIndex(
                name: "IX_BlockchainOperations_Status",
                table: "BlockchainOperations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BlockchainOperations_WalletId",
                table: "BlockchainOperations",
                column: "WalletId");

            migrationBuilder.CreateIndex(
                name: "IX_HolonNFTBindings_AvatarNFTId",
                table: "HolonNFTBindings",
                column: "AvatarNFTId");

            migrationBuilder.CreateIndex(
                name: "IX_HolonNFTBindings_HolonId",
                table: "HolonNFTBindings",
                column: "HolonId");

            migrationBuilder.CreateIndex(
                name: "IX_Holons_AvatarId",
                table: "Holons",
                column: "AvatarId");

            migrationBuilder.CreateIndex(
                name: "IX_Holons_ChainId",
                table: "Holons",
                column: "ChainId");

            migrationBuilder.CreateIndex(
                name: "IX_Holons_Name",
                table: "Holons",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Holons_ParentHolonId",
                table: "Holons",
                column: "ParentHolonId");

            migrationBuilder.CreateIndex(
                name: "IX_Holons_ProviderName",
                table: "Holons",
                column: "ProviderName");

            migrationBuilder.CreateIndex(
                name: "IX_QuestDependencies_DependsOnQuestId",
                table: "QuestDependencies",
                column: "DependsOnQuestId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestDependencies_QuestId",
                table: "QuestDependencies",
                column: "QuestId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestEdges_QuestId",
                table: "QuestEdges",
                column: "QuestId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestEdges_SourceNodeId_TargetNodeId",
                table: "QuestEdges",
                columns: new[] { "SourceNodeId", "TargetNodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuestNodes_NodeType",
                table: "QuestNodes",
                column: "NodeType");

            migrationBuilder.CreateIndex(
                name: "IX_QuestNodes_QuestId",
                table: "QuestNodes",
                column: "QuestId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestNodes_State",
                table: "QuestNodes",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_QuestNodeTemplates_IsPublic",
                table: "QuestNodeTemplates",
                column: "IsPublic");

            migrationBuilder.CreateIndex(
                name: "IX_QuestNodeTemplates_NodeType",
                table: "QuestNodeTemplates",
                column: "NodeType");

            migrationBuilder.CreateIndex(
                name: "IX_Quests_AvatarId",
                table: "Quests",
                column: "AvatarId");

            migrationBuilder.CreateIndex(
                name: "IX_Quests_DappSeriesId",
                table: "Quests",
                column: "DappSeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_Quests_Status",
                table: "Quests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Quests_TemplateId",
                table: "Quests",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestTemplateEdges_QuestTemplateId",
                table: "QuestTemplateEdges",
                column: "QuestTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestTemplateEdges_TemplateId",
                table: "QuestTemplateEdges",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestTemplateNodes_NodeTemplateId",
                table: "QuestTemplateNodes",
                column: "NodeTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestTemplateNodes_QuestTemplateId",
                table: "QuestTemplateNodes",
                column: "QuestTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestTemplateNodes_SlotId",
                table: "QuestTemplateNodes",
                column: "SlotId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestTemplateNodes_TemplateId",
                table: "QuestTemplateNodes",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestTemplates_AuthorAvatarId",
                table: "QuestTemplates",
                column: "AuthorAvatarId");

            migrationBuilder.CreateIndex(
                name: "IX_QuestTemplates_IsPublic",
                table: "QuestTemplates",
                column: "IsPublic");

            migrationBuilder.CreateIndex(
                name: "IX_STARODKs_AvatarId",
                table: "STARODKs",
                column: "AvatarId");

            migrationBuilder.CreateIndex(
                name: "IX_STARODKs_Name",
                table: "STARODKs",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_WalletNFTBindings_AvatarNFTId",
                table: "WalletNFTBindings",
                column: "AvatarNFTId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletNFTBindings_WalletId",
                table: "WalletNFTBindings",
                column: "WalletId");

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_Address",
                table: "Wallets",
                column: "Address");

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_AvatarId",
                table: "Wallets",
                column: "AvatarId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockchainOperations");

            migrationBuilder.DropTable(
                name: "HolonNFTBindings");

            migrationBuilder.DropTable(
                name: "QuestDependencies");

            migrationBuilder.DropTable(
                name: "QuestEdges");

            migrationBuilder.DropTable(
                name: "QuestNodes");

            migrationBuilder.DropTable(
                name: "QuestNodeTemplates");

            migrationBuilder.DropTable(
                name: "QuestTemplateEdges");

            migrationBuilder.DropTable(
                name: "QuestTemplateNodes");

            migrationBuilder.DropTable(
                name: "STARODKs");

            migrationBuilder.DropTable(
                name: "WalletNFTBindings");

            migrationBuilder.DropTable(
                name: "Holons");

            migrationBuilder.DropTable(
                name: "Quests");

            migrationBuilder.DropTable(
                name: "QuestTemplates");

            migrationBuilder.DropTable(
                name: "AvatarNFTs");

            migrationBuilder.DropTable(
                name: "Wallets");

            migrationBuilder.DropTable(
                name: "Avatars");
        }
    }
}
