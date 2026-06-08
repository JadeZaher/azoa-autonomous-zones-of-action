// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the avatar table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("avatar",
        Aggregate = "Avatar (Models/Avatar.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Wallets navigation list is NOT persisted here — owned by IWalletStore via wallet.avatar_id FK.")]
    [Slice("identity")]
    [Index("avatar_username", Fields = new[] { "username" }, Unique = true)]
    [Index("avatar_email", Fields = new[] { "email" }, Unique = true)]
    public partial class Avatar : ISurrealRecord
    {
        public const string SchemaNameConst = "avatar";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1, Type = "string")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [Column(Order = 3, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [Column(Order = 4, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("password_hash")]
        public string PasswordHash { get; set; } = string.Empty;

        [Column(Order = 5, Type = "option<string>")]
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [Column(Order = 6, Type = "option<string>")]
        [JsonPropertyName("first_name")]
        public string? FirstName { get; set; }

        [Column(Order = 7, Type = "option<string>")]
        [JsonPropertyName("last_name")]
        public string? LastName { get; set; }

        [Column(Order = 8, Type = "datetime")]
        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [Column(Order = 9, Type = "option<datetime>")]
        [JsonPropertyName("last_beamed_in_date")]
        public DateTimeOffset? LastBeamedInDate { get; set; }

        [Column(Order = 10, Type = "bool")]
        [Default("true")]
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }

        [Column(Order = 11, Type = "bool")]
        [Default("false")]
        [JsonPropertyName("is_verified")]
        public bool IsVerified { get; set; }

        [Column(Order = 12, Type = "int")]
        [Default("0")]
        [JsonPropertyName("karma")]
        public long Karma { get; set; }

        [Column(Order = 13, Type = "int")]
        [Default("1")]
        [JsonPropertyName("level")]
        public long Level { get; set; }
    }
}
