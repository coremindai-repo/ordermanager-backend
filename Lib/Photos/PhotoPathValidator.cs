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
    /// Stands in for the step id in a reference photo's path. Reference photos are
    /// captured at item entry, before any production step exists.
    ///
    /// Deliberately not a GUID: it can therefore never collide with a real step folder,
    /// and the path keeps its four-segment shape so both validators reason about the
    /// same structure.
    /// </summary>
    public const string ReferenceSegment = "reference";

    /// <summary>
    /// True only if the path is exactly {orderId}/{lineItemId}/reference/{guid}{ext}.
    ///
    /// Separate from <see cref="BelongsTo"/> rather than a loosened version of it —
    /// widening the step validator to also accept a non-GUID third segment would weaken
    /// the check guarding step photos in order to serve a different case.
    /// </summary>
    public static bool BelongsToLineItemReference(string? blobPath, Guid orderId, Guid lineItemId)
    {
        if (!HasSafeShape(blobPath, out var segments))
        {
            return false;
        }

        if (!Guid.TryParse(segments[0], out var pathOrderId) ||
            !Guid.TryParse(segments[1], out var pathLineItemId))
        {
            return false;
        }

        if (pathOrderId != orderId || pathLineItemId != lineItemId)
        {
            return false;
        }

        return string.Equals(segments[2], ReferenceSegment, StringComparison.Ordinal)
               && HasValidFileName(segments[3]);
    }

    /// <summary>
    /// True only if the path is exactly {orderId}/{lineItemId}/{stepId}/{guid}{ext},
    /// with all three ids matching the step being updated.
    /// </summary>
    public static bool BelongsTo(string? blobPath, Guid orderId, Guid lineItemId, Guid stepId)
    {
        if (!HasSafeShape(blobPath, out var segments))
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

        return HasValidFileName(segments[3]);
    }

    /// <summary>
    /// Traversal and structural checks shared by both validators — rejecting parent
    /// references, backslashes, absolute and URL forms before any segment is trusted.
    /// </summary>
    private static bool HasSafeShape(string? blobPath, out string[] segments)
    {
        segments = [];

        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return false;
        }

        if (blobPath.Contains("..", StringComparison.Ordinal) ||
            blobPath.Contains('\\', StringComparison.Ordinal) ||
            blobPath.StartsWith('/') ||
            blobPath.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        segments = blobPath.Split('/');
        return segments.Length == 4;
    }

    /// <summary>
    /// The final segment must be exactly {guid}{allowed extension}, matching what the
    /// upload targets issue. Validating the extension against the allow-list (rather
    /// than just "has a dot") is what rejects a trailing query string — a client
    /// echoing back the full SAS URL instead of the blobPath, for instance.
    /// </summary>
    private static bool HasValidFileName(string fileName)
    {
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
