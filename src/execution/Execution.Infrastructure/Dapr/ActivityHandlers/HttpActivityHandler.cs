using System.Text;
using System.Text.Json;
using Common.Workflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Execution.Domain.Services;

namespace Execution.Infrastructure.Dapr.ActivityHandlers;

public class HttpActivityHandler(
    HttpClient httpClient,
    ILogger<HttpActivityHandler> logger,
    IOptions<HttpActivityOptions> options)
    : IActivityHandler
{
    public ActivityKind Kind => ActivityKind.Http;

    public async Task<ActivityHandlerResult> ExecuteAsync(ActivityHandlerInput input, CancellationToken ct = default)
    {
        var httpMethod = DetermineHttpMethod(input.HttpMethod
                                             ?? throw new InvalidOperationException(
                                                 "HTTP method is required for HTTP activity"));
        var url = input.Url
                  ?? throw new InvalidOperationException("URL is required for HTTP activity");

        var log = WorkflowSourceLogger.For(logger, input.LogSource);

        log.LogDebug(
            "HTTP {HttpMethod} {Url} starting (execution {WorkflowExecutionId})",
            httpMethod, url, input.LogExecutionId);

        var startTime = DateTimeOffset.UtcNow;
        var request = new HttpRequestMessage(httpMethod, url);

        // Content-Type is a content header, so TryAddWithoutValidation on request.Headers silently
        // drops it. Reading it here is what makes application/x-www-form-urlencoded expressible —
        // without which a workflow cannot perform a standard OAuth token refresh.
        var requestContentType = ResolveContentType(input.Headers);

        if (!string.IsNullOrEmpty(input.Body))
            request.Content = new StringContent(input.Body, Encoding.UTF8, requestContentType);

        if (input.Headers != null)
            foreach (var (key, value) in input.Headers)
            {
                // Already applied to the content above; adding it here would either be dropped or
                // duplicate the header.
                if (string.Equals(key, "content-type", StringComparison.OrdinalIgnoreCase))
                    continue;

                request.Headers.TryAddWithoutValidation(key, value);
            }

        var response = await httpClient.SendAsync(request, ct);

        // Bytes first, text from them: one read, and the text is derived from the same bytes an
        // expression sees as `response.body`, using the charset the server declared (UTF-8 when it
        // declared none or one .NET does not know) — which is what ReadAsStringAsync did, minus
        // its own second look at the content.
        var responseBytes = await ReadBounded(response, options.Value.MaxResponseBodyBytes, ct);
        var responseContent = DecodeText(responseBytes, response.Content.Headers.ContentType?.CharSet);
        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;

        var statusCode = (int)response.StatusCode;
        var statusText = response.ReasonPhrase ?? string.Empty;

        var logLevel = statusCode switch
        {
            >= 200 and < 300 => LogLevel.Information,
            >= 400 and < 500 => LogLevel.Error,
            >= 500 => LogLevel.Warning,
            _ => LogLevel.Information
        };

        log.Log(logLevel,
            "HTTP {HttpMethod} {Url} completed: {StatusCode} {StatusText} in {DurationMs}ms (execution {WorkflowExecutionId})",
            httpMethod.Method, url, statusCode, statusText, (long)duration, input.LogExecutionId);

        // A non-2xx is reported, not thrown. Throwing here would flatten to a message string at the
        // Dapr boundary and lose the status and headers that on_failure rules branch on, so the
        // executor decides what the status means.
        // `output` is the parsed JSON body when the media type's essence is `application/json` or
        // ends in `+json`, and null otherwise (api/docs/binary-data.md § Reading a response).
        //
        // The `+json` half was missing: `application/problem+json` and `application/vnd.api+json`
        // are how a great many APIs answer, and their bodies were reaching `output` as null while
        // the body itself was plainly JSON. An author could only work around it by re-parsing the
        // body text, which is exactly what `output` exists to spare them.
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var parsedBody = IsJson(contentType) ? ParseJsonResponse(responseContent) : null;

        return new ActivityHandlerResult(
            statusCode,
            response.IsSuccessStatusCode,
            parsedBody,
            CollectHeaders(response),
            responseContent,
            responseBytes);
    }

    /// <summary>
    /// The body, up to <paramref name="limit"/> bytes. A response that exceeds it is abandoned and
    /// the activity fails with <c>UTOS-F101</c>.
    /// <para>
    /// Read from the stream rather than with <c>ReadAsByteArrayAsync</c>, because the point is to
    /// stop reading. Buffering the whole body and then measuring it would let a response the
    /// daemon has already decided to refuse decide how much memory it takes first.
    /// </para>
    /// <para>
    /// <c>content-length</c> is a hint only — missing on a chunked response and sometimes wrong —
    /// so the count of bytes actually read is what decides, not what the server claimed.
    /// </para>
    /// </summary>
    private static async Task<byte[]> ReadBounded(
        HttpResponseMessage response, long limit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;

            if (buffer.Length + read > limit)
                throw new ResponseTooLargeException(limit);

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Whether a media type's essence is JSON: <c>application/json</c>, or any type whose subtype
    /// ends in <c>+json</c> — the structured-syntax suffix of RFC 6839, which is how
    /// <c>application/problem+json</c> and <c>application/vnd.api+json</c> say they are JSON.
    /// </summary>
    private static bool IsJson(string? mediaType) =>
        mediaType is not null
        && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static string DecodeText(byte[] bytes, string? charset)
    {
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // An unknown or malformed charset: UTF-8 is the guess the previous read made too.
            }

        return encoding.GetString(bytes);
    }

    /// <summary>
    /// The content type an author declared, defaulting to JSON so workflows written before this was
    /// honoured behave exactly as they did.
    /// </summary>
    private static string ResolveContentType(Dictionary<string, string>? headers)
    {
        if (headers == null)
            return "application/json";

        foreach (var (key, value) in headers)
            if (string.Equals(key, "content-type", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
                // StringContent appends its own charset, so hand it the media type alone.
                return value.Split(';')[0].Trim();

        return "application/json";
    }

    /// <summary>
    /// Response and content headers merged into one map. Repeated headers are joined with ", " as
    /// RFC 9110 §5.3 allows, so a template sees one string per name rather than a shape that varies
    /// with how many values arrived.
    /// </summary>
    private static Dictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, values) in response.Headers)
            headers[key] = string.Join(", ", values);

        foreach (var (key, values) in response.Content.Headers)
            headers[key] = string.Join(", ", values);

        return headers;
    }

    private object? ParseJsonResponse(string responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
            return null;

        try
        {
            // Deliberately without WorkflowJson.Options, which is the one place in the daemon
            // that is true. A JsonElement re-serializes its raw token text, so an API's 10.0
            // crosses the Dapr boundary as "10.0" and narrows to a double on the far side.
            // Narrowing here instead would write it as "10" and hand the next activity an int,
            // changing division on live workflows. It collapses to int on the hop after that
            // either way — removing that limit needs declared response types, not a better guess.
            return JsonSerializer.Deserialize<object>(responseContent);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse response as JSON despite Content-Type: application/json");
            return null;
        }
    }

    private static HttpMethod DetermineHttpMethod(string method)
    {
        return method.ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            _ => throw new ArgumentException($"Unknown HTTP method: {method}")
        };
    }
}
