using Ganss.Xss;

namespace Framework.Communication;

/// <summary>
/// Single place where email HTML is sanitized before it is stored or rendered as markup in the document timeline.
/// Used for inbound email, outbound email recorded as a Communication, and again at render time so rows stored
/// before sanitization existed are safe too.
/// </summary>
public static class HtmlContentSanitizer
{
    // HtmlSanitizer instances are safe to share between threads once configured.
    private static readonly HtmlSanitizer Sanitizer = new();

    public static string? Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? html : Sanitizer.Sanitize(html);
}
