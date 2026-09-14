using System.Text;
using System.Text.Json;
using Common.Workflow;
using Microsoft.Extensions.Logging;
using Execution.Domain.Services;

namespace Execution.Infrastructure.Dapr.ActivityHandlers;

public class HttpActivityHandler(
    HttpClient httpClient,
    ILogger<HttpActivityHandler> logger)
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
        var responseContent = await response.Content.ReadAsStringAsync(ct);
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
        // Parse response body as output
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var parsedBody = contentType switch
        {
            "application/json" => ParseJsonResponse(responseContent),
            _ => null
        };

        return new ActivityHandlerResult(
            statusCode,
            response.IsSuccessStatusCode,
            parsedBody,
            CollectHeaders(response),
            responseContent);
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
