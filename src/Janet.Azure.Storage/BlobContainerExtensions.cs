using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Janet.Azure.Storage;

/// <summary>A blob's content as stored: its bytes and their type, for images as much as JSON.</summary>
/// <param name="Content">The bytes.</param>
/// <param name="ContentType">The type they were stored with, e.g. image/png.</param>
public sealed record BlobContent(BinaryData Content, string ContentType)
{
    /// <summary>The type JSON documents are stored with.</summary>
    public const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>A value as a JSON document, ready to store.</summary>
    /// <param name="value">The value.</param>
    /// <param name="options">How to write it; the serializer's defaults when null.</param>
    public static BlobContent FromJson<T>(T value, JsonSerializerOptions? options = null) =>
        new(BinaryData.FromObjectAsJson(value, options), JsonContentType);

    /// <summary>
    /// The content read as a JSON document. A document that is JSON <c>null</c> is refused rather
    /// than returned, so a caller never mistakes a damaged blob for a missing value.
    /// </summary>
    /// <param name="options">How to read it; the serializer's defaults when null.</param>
    /// <exception cref="JsonException">The content is not JSON of that shape.</exception>
    /// <exception cref="InvalidDataException">The content is JSON <c>null</c>.</exception>
    public T ReadJson<T>(JsonSerializerOptions? options = null) =>
        Content.ToObjectFromJson<T>(options)
        ?? throw new InvalidDataException($"The blob holds JSON null, not a {typeof(T).Name}.");
}

/// <summary>
/// Binary and JSON reads and writes on a container, the way an app that never overwrites wants them.
/// </summary>
public static class BlobContainerExtensions
{
    /// <summary>
    /// Creates the blob only if no blob has that name, and never replaces one that does: the
    /// condition is enforced by the service (If-None-Match: *), so two writers racing for a name
    /// cannot both win.
    /// </summary>
    /// <param name="container">The container.</param>
    /// <param name="name">The blob's name.</param>
    /// <param name="content">The bytes to store.</param>
    /// <param name="contentType">Their type, stored with them, e.g. image/png.</param>
    /// <param name="metadata">Optional metadata; values must be ASCII, as the service requires.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>True when created; false when a blob with that name already existed.</returns>
    public static async Task<bool> TryCreateAsync(
        this BlobContainerClient container, string name, BinaryData content, string contentType,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        try
        {
            await container.GetBlobClient(name).UploadAsync(content, CreateOnly(contentType, metadata), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }

    /// <summary>Reads the blob's bytes with the type they were stored with.</summary>
    /// <param name="container">The container.</param>
    /// <param name="name">The blob's name.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    public static async Task<BlobContent> ReadContentAsync(
        this BlobContainerClient container, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var result = await container.GetBlobClient(name).DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return new BlobContent(result.Value.Content, result.Value.Details.ContentType);
    }

    /// <summary>
    /// Creates a JSON document only if no blob has that name; see <see cref="TryCreateAsync"/>.
    /// </summary>
    /// <param name="container">The container.</param>
    /// <param name="name">The blob's name.</param>
    /// <param name="value">The value to store as JSON.</param>
    /// <param name="options">How to write it; the serializer's defaults when null.</param>
    /// <param name="metadata">Optional metadata; values must be ASCII, as the service requires.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>True when created; false when a blob with that name already existed.</returns>
    public static Task<bool> TryCreateJsonAsync<T>(
        this BlobContainerClient container, string name, T value, JsonSerializerOptions? options = null,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        var json = BlobContent.FromJson(value, options);
        return container.TryCreateAsync(name, json.Content, json.ContentType, metadata, cancellationToken);
    }

    /// <summary>Reads a JSON document; see <see cref="BlobContent.ReadJson{T}"/>.</summary>
    /// <param name="container">The container.</param>
    /// <param name="name">The blob's name.</param>
    /// <param name="options">How to read it; the serializer's defaults when null.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    public static async Task<T> ReadJsonAsync<T>(
        this BlobContainerClient container, string name, JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default) =>
        (await container.ReadContentAsync(name, cancellationToken).ConfigureAwait(false)).ReadJson<T>(options);

    // The upload options that make a write a create: the If-None-Match condition, the type, the metadata.
    internal static BlobUploadOptions CreateOnly(string contentType, IReadOnlyDictionary<string, string>? metadata) => new()
    {
        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
        HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
        Metadata = metadata?.ToDictionary(kv => kv.Key, kv => kv.Value),
    };
}
