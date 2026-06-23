using FluentAssertions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Providers.Kyc;

namespace AZOA.WebAPI.Tests.Kyc;

public class ManualKycProviderServiceTests
{
    private readonly ManualKycProviderService _provider = new();

    private static SubmitKycDocumentModel ValidDoc(string? mime = "image/png", long? size = 1024) => new()
    {
        Type = KycDocumentType.GOVERNMENT_ID,
        FileUrl = "https://blob.example/doc.png",
        FileName = "doc.png",
        MimeType = mime,
        FileSizeBytes = size
    };

    [Fact]
    public async Task ValidateDocuments_EmptyList_ReturnsError()
    {
        var result = await _provider.ValidateDocumentsAsync(new List<SubmitKycDocumentModel>());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("At least one document");
    }

    [Fact]
    public async Task ValidateDocuments_MissingFileUrl_ReturnsError()
    {
        var doc = ValidDoc();
        doc.FileUrl = "";

        var result = await _provider.ValidateDocumentsAsync(new[] { doc });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("file URL");
    }

    [Fact]
    public async Task ValidateDocuments_DisallowedMime_ReturnsError()
    {
        var result = await _provider.ValidateDocumentsAsync(new[] { ValidDoc(mime: "application/x-msdownload") });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("unsupported file type");
    }

    [Fact]
    public async Task ValidateDocuments_OversizeFile_ReturnsError()
    {
        var result = await _provider.ValidateDocumentsAsync(new[] { ValidDoc(size: 11L * 1024 * 1024) });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("maximum file size");
    }

    [Fact]
    public async Task ValidateDocuments_ValidSet_ReturnsSuccess()
    {
        var result = await _provider.ValidateDocumentsAsync(new[] { ValidDoc(), ValidDoc(mime: "application/pdf") });

        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
    }

    [Fact]
    public async Task CreateSession_ReturnsAvatarIdAsPseudoSession()
    {
        var avatarId = Guid.NewGuid();

        var result = await _provider.CreateSessionAsync(avatarId, new List<KycDocumentModel>());

        result.IsError.Should().BeFalse();
        result.Result.Should().Be(avatarId.ToString("N"));
    }

    [Fact]
    public async Task GetSessionStatus_ReturnsPending()
    {
        var result = await _provider.GetSessionStatusAsync("session");

        result.IsError.Should().BeFalse();
        result.Result.Should().Be(KycStatus.PENDING);
    }

    [Fact]
    public async Task HandleWebhook_ReturnsPending()
    {
        var result = await _provider.HandleWebhookAsync("{}");

        result.IsError.Should().BeFalse();
        result.Result.Should().Be(KycStatus.PENDING);
    }
}
