using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;

namespace OrderManager.Backend.Lib.Photos;

public sealed record PhotoUploadTarget(string BlobPath, string UploadUrl, DateTimeOffset ExpiresAt);

public sealed record PhotoBlobInfo(long ContentLength, string? ContentType);

public interface IPhotoStorage
{
    /// <summary>Issues a narrowly-scoped, write-only, short-lived SAS for one new blob.</summary>
    PhotoUploadTarget CreateUploadTarget(Guid orderId, Guid lineItemId, Guid stepId, string fileExtension);

    /// <summary>
    /// The same, for a line item's reference photo — captured at item entry, before any
    /// production step exists. Identical scoping and lifetime; only the path differs.
    /// </summary>
    PhotoUploadTarget CreateReferenceUploadTarget(Guid orderId, Guid lineItemId, string fileExtension);

    /// <summary>Mints a fresh short-lived read URL for a stored blob path.</summary>
    string CreateReadUrl(string blobPath);

    /// <summary>
    /// Reads a blob's properties so the confirmation step can validate what was
    /// actually uploaded. Returns null if the blob does not exist.
    /// </summary>
    Task<PhotoBlobInfo?> GetBlobInfoAsync(string blobPath);
}

/// <summary>
/// Photos are uploaded by the device straight to Blob storage using a SAS this class
/// issues; the bytes never pass through the Function. Consequences worth keeping in
/// mind when changing this:
///
/// - The SAS is deliberately narrow: one named blob, write/create only, minutes-long.
///   It cannot list, read, delete, or touch any other blob.
/// - Because the backend never sees the bytes, content-type and size are validated
///   after the fact via <see cref="GetBlobInfoAsync"/> when the client confirms the
///   upload. Anything not validated there is effectively unvalidated.
/// - Only blob *paths* are persisted. Read URLs are generated per response and expire
///   quickly, so a URL captured from a response or a log stops working shortly after.
/// </summary>
public sealed class PhotoStorage : IPhotoStorage
{
    /// <summary>
    /// Long enough for a large photo over a poor factory-floor connection, short
    /// enough that a leaked upload URL is near-useless. Write-only and single-blob,
    /// so the worst case is one unreferenced blob.
    /// </summary>
    private static readonly TimeSpan UploadWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Covers viewing a screen's photos, including scrolling back to them, without
    /// being long enough to serve as a shareable or cacheable link.
    /// </summary>
    private static readonly TimeSpan ReadWindow = TimeSpan.FromMinutes(15);

    /// <summary>Clock-skew allowance so a just-issued SAS is not rejected as future-dated.</summary>
    private static readonly TimeSpan SkewAllowance = TimeSpan.FromMinutes(5);

    private readonly BlobContainerClient _container;
    private readonly StorageSharedKeyCredential _credential;
    private readonly string _containerName;

    public PhotoStorage(IConfiguration configuration)
    {
        var connectionString = configuration["PHOTO_STORAGE_CONNECTION_STRING"]
            ?? throw new InvalidOperationException("PHOTO_STORAGE_CONNECTION_STRING is not configured");
        _containerName = configuration["PHOTO_CONTAINER_NAME"] ?? "production-photos";

        var serviceClient = new BlobServiceClient(connectionString);
        _container = serviceClient.GetBlobContainerClient(_containerName);

        // SAS generation needs the shared key; parse it out of the connection string
        // rather than requiring a second setting that could drift out of sync.
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

        if (!parts.TryGetValue("AccountName", out var accountName) ||
            !parts.TryGetValue("AccountKey", out var accountKey))
        {
            throw new InvalidOperationException(
                "PHOTO_STORAGE_CONNECTION_STRING must contain AccountName and AccountKey to issue SAS tokens");
        }

        // AccountKey is base64 and contains '=' padding, which Split(2) preserves.
        _credential = new StorageSharedKeyCredential(accountName, accountKey);
    }

    public PhotoUploadTarget CreateUploadTarget(Guid orderId, Guid lineItemId, Guid stepId, string fileExtension)
    {
        var extension = NormaliseExtension(fileExtension);
        var blobPath = $"{orderId}/{lineItemId}/{stepId}/{Guid.NewGuid()}{extension}";
        var expiresAt = DateTimeOffset.UtcNow.Add(UploadWindow);

        // Create + Write only: enough to PUT one new blob, nothing else.
        var url = BuildSasUri(blobPath, BlobSasPermissions.Create | BlobSasPermissions.Write, expiresAt);

        return new PhotoUploadTarget(blobPath, url, expiresAt);
    }

    public PhotoUploadTarget CreateReferenceUploadTarget(Guid orderId, Guid lineItemId, string fileExtension)
    {
        var extension = NormaliseExtension(fileExtension);
        // "reference" in place of the step id — see PhotoPathValidator.ReferenceSegment.
        var blobPath = $"{orderId}/{lineItemId}/{PhotoPathValidator.ReferenceSegment}/{Guid.NewGuid()}{extension}";
        var expiresAt = DateTimeOffset.UtcNow.Add(UploadWindow);

        var url = BuildSasUri(blobPath, BlobSasPermissions.Create | BlobSasPermissions.Write, expiresAt);

        return new PhotoUploadTarget(blobPath, url, expiresAt);
    }

    public string CreateReadUrl(string blobPath) =>
        BuildSasUri(blobPath, BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(ReadWindow));

    public async Task<PhotoBlobInfo?> GetBlobInfoAsync(string blobPath)
    {
        var blob = _container.GetBlobClient(blobPath);

        if (!await blob.ExistsAsync())
        {
            return null;
        }

        var properties = await blob.GetPropertiesAsync();
        return new PhotoBlobInfo(properties.Value.ContentLength, properties.Value.ContentType);
    }

    private string BuildSasUri(string blobPath, BlobSasPermissions permissions, DateTimeOffset expiresAt)
    {
        var builder = new BlobSasBuilder
        {
            BlobContainerName = _containerName,
            BlobName = blobPath,
            Resource = "b", // this blob only — never the container
            StartsOn = DateTimeOffset.UtcNow.Subtract(SkewAllowance),
            ExpiresOn = expiresAt,
        };
        builder.SetPermissions(permissions);

        var sas = builder.ToSasQueryParameters(_credential).ToString();
        return $"{_container.GetBlobClient(blobPath).Uri}?{sas}";
    }

    private static string NormaliseExtension(string fileExtension)
    {
        var extension = fileExtension.Trim().ToLowerInvariant();
        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }

        // Restrictive on purpose: the blob name is attacker-influenced, and this is the
        // only place its shape is decided. Same allow-list the confirmation step checks.
        return PhotoPathValidator.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? extension
            : throw new AppException(
                Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest,
                "VALIDATION_ERROR",
                $"fileExtension must be one of: {string.Join(", ", PhotoPathValidator.AllowedExtensions)}");
    }
}
