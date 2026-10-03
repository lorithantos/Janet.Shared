using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Janet.Azure.Storage;
using Xunit;

namespace Janet.Shared.Tests;

/// <summary>
/// The create-only write and the typed read, against the SDK's own mocking seams (its clients'
/// virtual members), so no storage account is reached.
/// </summary>
public class BlobContainerExtensionsTests
{
    private static readonly BinaryData Png = new(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF });

    [Fact]
    public async Task A_create_is_conditional_on_no_blob_having_the_name()
    {
        var blob = new RecordingBlob();
        var created = await new FakeContainer(blob).TryCreateAsync("images/a", Png, "image/png",
            new Dictionary<string, string> { ["name"] = "a" });

        Assert.True(created);
        Assert.Equal(ETag.All, blob.Options!.Conditions.IfNoneMatch);
        Assert.Equal("image/png", blob.Options.HttpHeaders.ContentType);
        Assert.Equal("a", blob.Options.Metadata["name"]);
        Assert.Equal(Png.ToArray(), blob.Uploaded!.ToArray());
    }

    [Fact]
    public async Task A_name_already_taken_is_reported_not_overwritten()
    {
        var blob = new RecordingBlob { Conflict = true };

        Assert.False(await new FakeContainer(blob).TryCreateAsync("images/a", Png, "image/png"));
    }

    [Fact]
    public async Task A_read_returns_the_bytes_with_their_type()
    {
        var blob = new RecordingBlob { Stored = Png, StoredType = "image/png" };

        var content = await new FakeContainer(blob).ReadContentAsync("images/a");

        Assert.Equal(Png.ToArray(), content.Content.ToArray());
        Assert.Equal("image/png", content.ContentType);
    }

    private sealed record Look(string AccentColour, int Version);

    private static readonly System.Text.Json.JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    [Fact]
    public async Task A_json_document_is_created_only_if_absent_and_typed_as_json()
    {
        var blob = new RecordingBlob();

        Assert.True(await new FakeContainer(blob).TryCreateJsonAsync("look.json", new Look("#997c61", 2), CamelCase));
        Assert.Equal(ETag.All, blob.Options!.Conditions.IfNoneMatch);
        Assert.Equal(BlobContent.JsonContentType, blob.Options.HttpHeaders.ContentType);
        Assert.Equal("""{"accentColour":"#997c61","version":2}""", blob.Uploaded!.ToString());
    }

    [Fact]
    public async Task A_json_document_reads_back_as_the_value_written()
    {
        var written = BlobContent.FromJson(new Look("#225E3E", 2), CamelCase);
        var blob = new RecordingBlob { Stored = written.Content, StoredType = written.ContentType };

        Assert.Equal(new Look("#225E3E", 2), await new FakeContainer(blob).ReadJsonAsync<Look>("look.json", CamelCase));
    }

    [Fact]
    public void A_json_null_document_is_refused_rather_than_read_as_nothing()
    {
        var content = new BlobContent(BinaryData.FromString("null"), BlobContent.JsonContentType);

        Assert.Throws<System.IO.InvalidDataException>(() => content.ReadJson<Look>());
    }

    private sealed class FakeContainer(BlobClient blob) : BlobContainerClient
    {
        public override BlobClient GetBlobClient(string blobName) => blob;
    }

    private sealed class RecordingBlob : BlobClient
    {
        public bool Conflict { get; init; }
        public BinaryData? Stored { get; init; }
        public string StoredType { get; init; } = "";
        public BlobUploadOptions? Options { get; private set; }
        public BinaryData? Uploaded { get; private set; }

        public override Task<Response<BlobContentInfo>> UploadAsync(
            BinaryData content, BlobUploadOptions options, CancellationToken cancellationToken = default)
        {
            if (Conflict) throw new RequestFailedException(409, "BlobAlreadyExists");
            Uploaded = content;
            Options = options;
            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobContentInfo(ETag.All, DateTimeOffset.UtcNow, null, null, null, null, 0),
                new NoResponse()));
        }

        public override Task<Response<BlobDownloadResult>> DownloadContentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobDownloadResult(Stored, BlobsModelFactory.BlobDownloadDetails(contentType: StoredType)),
                new NoResponse()));
    }

    private sealed class NoResponse : Response
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override System.IO.Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<global::Azure.Core.HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value) { value = ""; return false; }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = []; return false; }
    }
}
