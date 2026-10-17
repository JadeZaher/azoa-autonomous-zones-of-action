namespace AZOA.WebAPI.Models.Deployments;

public sealed record AvmProgramInput(string Kind, string Data);
public sealed record AvmStateSchema(ulong Integers, ulong Bytes);
public sealed record AvmBoxReference(ulong AppIndex, string NameBase64);
public sealed record AvmApplicationArtifact(AvmProgramInput Approval, AvmProgramInput ClearState,
    AvmStateSchema GlobalSchema, AvmStateSchema LocalSchema, uint ExtraPages,
    IReadOnlyList<string> ArgumentsBase64, IReadOnlyList<string> Accounts,
    IReadOnlyList<string> ForeignApplications, IReadOnlyList<string> ForeignAssets,
    IReadOnlyList<AvmBoxReference> Boxes);
public sealed record AvmNativeInput(NativeArtifact Artifact, string Sender, string Operation = "Create",
    string? ApplicationId = null);

public sealed record SolanaElfArtifact(string ElfBase64, string Loader = "loader-v3", string? SourceDigest = null);
public sealed record SolanaInstructionAccount(string Address, bool Writable, bool Signer);
public sealed record SolanaInstructionArtifact(string ProgramId, IReadOnlyList<SolanaInstructionAccount> Accounts,
    string DataBase64, string? ExpectedProgramDataHash = null);
public sealed record SolanaMetadataCreator(string Address, bool Verified, byte Share);
public sealed record SolanaAccountArtifact(string Operation, string Mint, string Owner,
    string? TokenAccount = null, string? Destination = null, string? Name = null, string? Symbol = null,
    string? Uri = null, ushort? SellerFeeBasisPoints = null, bool? Mutable = null,
    string? UpdateAuthority = null, string? ExpectedMetadataHash = null,
    IReadOnlyList<SolanaMetadataCreator>? Creators = null, bool UnwrapNative = false);
public sealed record SolanaNativeInput(NativeArtifact Artifact, string Payer, string Authority,
    string Operation = "Create", string? ProgramId = null, bool Immutable = false,
    uint MaximumDataLength = 0, string? RentRecipient = null, string? ExpectedProgramDataHash = null);

public sealed record EvmContractArtifact(string InitCodeHex, string ExpectedRuntimeCodeHex,
    string? ConstructorSignature = null, IReadOnlyList<System.Text.Json.JsonElement>? ConstructorArguments = null);
public sealed record EvmCallArtifact(string ContractAddress, string ExpectedCodeHash, string Method,
    IReadOnlyList<System.Text.Json.JsonElement> Arguments);
public sealed record EvmNativeInput(NativeArtifact Artifact, string Sender, string ValueBaseUnits = "0");
public sealed record EvmReadCall(string ContractAddress, string Method,
    IReadOnlyList<System.Text.Json.JsonElement> Arguments, IReadOnlyList<string> OutputTypes, string? FromAddress = null);
public sealed record ContractQueryRequest(string ChainType, string Network, EvmReadCall Call);
