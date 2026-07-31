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

        logger.LogDebug(
            "HTTP {HttpMethod} {Url} starting (execution {WorkflowExecutionId})",
            httpMethod, url, input.LogExecutionId);

        var startTime = DateTimeOffset.UtcNow;
        var request = new HttpRequestMessage(httpMethod, url);

        if (!string.IsNullOrEmpty(input.Body))
            request.Content = new StringContent(input.Body, Encoding.UTF8, "application/json");

        if (input.Headers != null)
            foreach (var (key, value) in input.Headers)
                request.Headers.TryAddWithoutValidation(key, value);

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

        logger.Log(logLevel,
            "HTTP {HttpMethod} {Url} completed: {StatusCode} {StatusText} in {DurationMs}ms (execution {WorkflowExecutionId})",
            httpMethod.Method, url, statusCode, statusText, (long)duration, input.LogExecutionId);

        // TODO: Add on-error handler support - check if status code should be handled before throwing
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"HTTP {httpMethod} request failed with status {statusCode}: {responseContent}",
                null,
                response.StatusCode);

        // Parse response body as output
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var parsedBody = contentType switch
        {
            "application/json" => ParseJsonResponse(responseContent),
            _ => null
        };

        return new ActivityHandlerResult(statusCode, true, parsedBody);
    }

    private object? ParseJsonResponse(string responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
            return null;

        try
        {
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
