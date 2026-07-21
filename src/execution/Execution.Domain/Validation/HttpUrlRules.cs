namespace Execution.Domain.Validation;

/// <summary>
/// Shape rules for HTTP activity URLs, applied at two points.
///
/// At load time a URL containing a template expression cannot be checked — its scheme or
/// host may come from <c>{{ env.baseUrl }}</c> and is unknown until the run supplies it.
/// Such URLs are accepted then and checked again once rendered, so a bad value still
/// fails with the same diagnostic rather than surfacing as a raw HTTP client error.
/// </summary>
public static class HttpUrlRules
{
    public const string EmptyError = "HTTP activity URL cannot be empty";
    public const string FormatError = "Invalid URL format";
    public const string SchemeError = "Unsupported URL scheme (only HTTP/HTTPS allowed)";

    /// <summary>
    /// True when the URL still contains an unrendered template expression, so its final
    /// shape is not yet known.
    /// </summary>
    public static bool HasTemplateExpression(string url) => url.Contains("{{");

    /// <summary>
    /// Validates a fully-rendered URL: absolute, and http/https.
    /// </summary>
    public static bool IsValidRendered(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == "http" || uri.Scheme == "https";
    }

    /// <summary>
    /// Validates a URL as authored, deferring anything still holding a template.
    /// </summary>
    public static bool IsValidAuthored(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return HasTemplateExpression(url) || IsValidRendered(url);
    }

    /// <summary>
    /// The reason <paramref name="url"/> is not a usable HTTP URL. Only meaningful when
    /// validation failed.
    /// </summary>
    public static string GetError(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return EmptyError;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return FormatError;

        if (uri.Scheme != "http" && uri.Scheme != "https")
            return SchemeError;

        return FormatError;
    }
}
