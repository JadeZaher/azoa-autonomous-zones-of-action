using System.Text.RegularExpressions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using Moq;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.Providers.Stores.Surreal;

/// <summary>
/// D19 issuer-column invariants on <see cref="SurrealNftStore"/> — the writer the whole
/// revocation rail hangs off.
///
/// <para><b>Why some of these read source.</b> The write paths go through
/// <c>ISurrealExecutor.ExecuteAsync</c>, whose <c>SurrealResponse</c> cannot be
/// constructed in-process (the same limitation recorded on
/// <c>WideAmountAndValueStampTests</c>), so the call itself cannot be driven from a unit
/// test. What is under test here is not a computation but the EXISTENCE of a
/// consultation and the shape of one SQL guard — "a control that is never invoked" is
/// exactly the defect being fixed, and a source assertion fails the moment the call is
/// deleted. Comments are stripped first, so a mention in prose can never satisfy one of
/// these. The behavioural round trip belongs to the store's integration suite.</para>
/// </summary>
public sealed class SurrealNftStoreCredentialIssuerTests
{
    private static readonly string StoreSource = File.ReadAllText(
        Path.Combine(ResolveRepoRoot(), "Providers", "Stores", "Surreal", "SurrealNftStore.cs"));

    // ── The issuance path must actually stamp an issuer ───────────────────────

    /// <summary>
    /// Without this call every soulbound credential is minted with a null
    /// <c>issuer_user_id</c>, and since that column is the only input to the revoke
    /// authorization decision, EVERY revocation dies at "no recorded issuer". The rail
    /// then looks complete and is inert. Delete the call and this test fails.
    /// </summary>
    [Fact]
    public void UpsertUserNFT_ConsultsTheIssuerWriter_ForASoulboundRow()
    {
        var code = CodeOf("public async Task<AZOAResult<IUserNFT>> UpsertUserNFTAsync");

        code.Should().Contain("SetIssuerUserIdAsync(",
            "the mint path is the only production writer of issuer_user_id; without this "
            + "call the revocation rail has no authorization anchor to read");
        code.Should().Contain("IsSoulbound",
            "only soulbound rows are credentials; an ordinary NFT gets no issuer stamp");
        code.Should().Contain("userNFT.UserId",
            "the issuer recorded is the minting party carried on the row, never a caller-supplied field");
    }

    /// <summary>
    /// A mint that silently failed to record an issuer produces a credential nobody can
    /// ever revoke — strictly worse than no credential at all.
    /// </summary>
    [Fact]
    public void UpsertUserNFT_FailsTheMint_WhenTheIssuerCannotBeRecorded()
    {
        var code = CodeOf("public async Task<AZOAResult<IUserNFT>> UpsertUserNFTAsync");

        code.Should().MatchRegex(@"stamped\s*\.\s*IsError",
            "the stamp result must be inspected, not fire-and-forget");
        code.Should().Contain("IsError = true",
            "an unrecorded issuer must surface as a failed mint");
    }

    // ── …and the column must be write-once ────────────────────────────────────

    /// <summary>
    /// The issuer decides who may claw a credential back. A later write that re-pointed
    /// it would hand that authority to another party, so the guard belongs in the SQL
    /// rather than in a caller's discipline.
    /// </summary>
    [Fact]
    public void SetIssuerUserId_IsGuardedWriteOnce_InTheStatementItself()
    {
        var code = CodeOf("public async Task<AZOAResult<bool>> SetIssuerUserIdAsync");

        code.Should().MatchRegex(@"WHERE\s+issuer_user_id\s+IS\s+NONE",
            "the write-once guard must be enforced by the database, not by convention");
        code.Should().Contain("already records a different issuer",
            "re-pointing the anchor at another party must be refused, not silently ignored");
    }

    [Fact]
    public async Task SetIssuerUserId_RefusesEmptyIdentifiers_WithoutTouchingTheDatabase()
    {
        var executor = new Mock<ISurrealExecutor>(MockBehavior.Strict);
        var store = new SurrealNftStore(executor.Object);

        var noCredential = await store.SetIssuerUserIdAsync(Guid.Empty, Guid.NewGuid());
        var noIssuer = await store.SetIssuerUserIdAsync(Guid.NewGuid(), Guid.Empty);

        noCredential.IsError.Should().BeTrue();
        noIssuer.IsError.Should().BeTrue("an empty issuer would mean an unrevocable credential");
        executor.VerifyNoOtherCalls();
    }

    // ── A confirmed revocation must move ownership, and only ownership ────────

    /// <summary>
    /// The ownership move is what stops every AZOA read surface reporting a revoked
    /// credential as still held. It must re-point the SAME live row: setting
    /// <c>is_current = false</c> would drop the asset out of every credential view, and
    /// rewriting <c>issuer_user_id</c> would change who issued a credential merely
    /// because it came home.
    /// </summary>
    [Fact]
    public void RecordCredentialRevoked_RepointsTheLiveRow_WithoutRewritingIssuerOrCurrency()
    {
        var code = CodeOf("public async Task<AZOAResult<bool>> RecordCredentialRevokedAsync");

        code.Should().Contain("user_id = $_issuer");
        code.Should().Contain("current_owner =");
        code.Should().NotContain("issuer_user_id",
            "the party that issued a credential does not change by taking it back");
        code.Should().NotContain("is_current",
            "a clawback moved the asset, it did not retire the row");
    }

    [Fact]
    public async Task RecordCredentialRevoked_RefusesAnIncompleteMove_WithoutTouchingTheDatabase()
    {
        var executor = new Mock<ISurrealExecutor>(MockBehavior.Strict);
        var store = new SurrealNftStore(executor.Object);

        var noCredential = await store.RecordCredentialRevokedAsync(
            Guid.Empty, Guid.NewGuid(), "ISSUERADDRESS");
        var noIssuer = await store.RecordCredentialRevokedAsync(
            Guid.NewGuid(), Guid.Empty, "ISSUERADDRESS");
        var noAddress = await store.RecordCredentialRevokedAsync(
            Guid.NewGuid(), Guid.NewGuid(), "   ");

        noCredential.IsError.Should().BeTrue();
        noIssuer.IsError.Should().BeTrue();
        noAddress.IsError.Should().BeTrue("a blank owner address would erase the holder of record");
        executor.VerifyNoOtherCalls();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The COMMENT-FREE text of one member, from its signature up to the next member
    /// declaration at class-body indentation. Comments are stripped so a claim written in
    /// prose can never satisfy an assertion about what the code does.
    /// </summary>
    private static string CodeOf(string signature)
    {
        var start = StoreSource.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterOrEqualTo(0, $"'{signature}' must exist in SurrealNftStore.cs");

        var rest = StoreSource[(start + signature.Length)..];
        var next = Regex.Match(rest, @"\r?\n    (?:public|private|internal|protected)\s");
        var body = next.Success ? rest[..next.Index] : rest;

        return string.Join('\n', body
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // Probe for the solution file, NOT for "Providers/Stores/Surreal":
            // this TEST project mirrors that directory path (this file lives in it),
            // so a directory probe matches the test tree first and resolves the repo
            // root to tests/AZOA.WebAPI.Tests.
            if (File.Exists(Path.Combine(directory.FullName, "azoa.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the AZOA repository root.");
    }
}
