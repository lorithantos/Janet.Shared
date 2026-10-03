using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Janet.Azure.Storage;

/// <summary>A blob's content as stored: its bytes and their type, for images as much as JSON.</summary>
/// <param name="Content">The bytes.</param>
/// <param name="ContentType">The type they were stored with, e.g. image/png.</param>
public sealed record BlobContent(BinaryData Content, string ContentType);

/// <summary>
/// Binary reads and writes on a container, the way an app that never overwrites wants them.
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

    // The upload options that make a write a create: the If-None-Match condition, the type, the metadata.
    internal static BlobUploadOptions CreateOnly(string contentType, IReadOnlyDictionary<string, string>? metadata) => new()
    {
        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
        HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
        Metadata = metadata?.ToDictionary(kv => kv.Key, kv => kv.Value),
    };
}
