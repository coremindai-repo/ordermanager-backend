namespace OrderManager.Backend.Lib.Photos;

/// <summary>
/// Blob paths are supplied by the client on the confirmation call, so they cannot be
/// trusted. A caller could otherwise attach a blob belonging to a different order,
/// line item, or step — or a path crafted to escape the expected layout.
///
/// Pure and dependency-free so every rejection case is unit-tested.
/// </summary>
public static class PhotoPathValidator
{
    /// <summary>
    /// The only extensions this system ever issues or accepts. Shared with
    /// <see cref="PhotoStorage"/> so the SAS-issuing and confirmation paths cannot
    /// drift apart.
    /// </summary>
    public static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".heic", ".webp"];

    /// <summary>
    /// True only if the path is exactly {orderId}/{lineItemId}/{stepId}/{guid}{ext},
    /// with all three ids matching the step being updated.
    /// </summary>
    public static bool BelongsTo(string? blobPath, Guid orderId, Guid lineItemId, Guid stepId)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return false;
        }

        // Reject traversal and absolute/UNC forms before looking at structure.
        if (blobPath.Contains("..", StringComparison.Ordinal) ||
            blobPath.Contains('\\', StringComparison.Ordinal) ||
            blobPath.StartsWith('/') ||
            blobPath.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = blobPath.Split('/');
        if (segments.Length != 4)
        {
            return false;
        }

        if (!Guid.TryParse(segments[0], out var pathOrderId) ||
            !Guid.TryParse(segments[1], out var pathLineItemId) ||
            !Guid.TryParse(segments[2], out var pathStepId))
        {
            return false;
        }

        if (pathOrderId != orderId || pathLineItemId != lineItemId || pathStepId != stepId)
        {
            return false;
        }

        // Final segment must be exactly {guid}{allowed extension}, matching what
        // CreateUploadTarget issues. Validating the extension against the allow-list
        // (rather than just "has a dot") is what rejects trailing query strings — a
        // client echoing back the full SAS URL instead of the blobPath, for instance.
        var fileName = segments[3];
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1)
        {
            return false;
        }

        if (!AllowedExtensions.Contains(fileName[dot..], StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return Guid.TryParse(fileName[..dot], out _);
    }
}
