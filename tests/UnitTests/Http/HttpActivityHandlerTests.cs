using System.Net;
using System.Text;
using Common.SeedWork;
using Execution.Domain.Services;
using Execution.Infrastructure.Dapr.ActivityHandlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace UnitTests.Http;

public class HttpActivityHandlerTests
{
    /// <summary>
    /// Captures the outgoing request and returns a canned response, so the handler can be
    /// exercised without a socket.
    /// </summary>
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Captured { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured = request;

            // The body must be read now — the message is disposed once the handler returns.
            if (request.Content != null)
                await request.Content.LoadIntoBufferAsync(cancellationToken);

            return response;
        }
    }

    private static (HttpActivityHandler Handler, StubHandler Stub) Build(HttpResponseMessage response)
    {
        var stub = new StubHandler(response);
        var handler = new HttpActivityHandler(
            new HttpClient(stub), NullLogger<HttpActivityHandler>.Instance);

        return (handler, stub);
    }

    private static ActivityHandlerInput Request(
        string? body = null, Dictionary<string, string>? headers = null, string method = "POST")
    {
        return new ActivityHandlerInput(
            ExecutionId.New(), ExecutionId.New(),
            "https://api.example.com/thing", method, body, headers);
    }

    private static HttpResponseMessage Ok(string content = "{}", string contentType = "application/json")
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, contentType)
        };
    }

    [Fact]
    public async Task Author_declared_content_type_reaches_the_request_content()
    {
        var (handler, stub) = Build(Ok());

        await handler.ExecuteAsync(Request(
            body: "grant_type=refresh_token&refresh_token=abc",
            headers: new Dictionary<string, string> { ["content-type"] = "application/x-www-form-urlencoded" }));

        // Without this, a standard OAuth token refresh is not expressible at all.
        Assert.Equal("application/x-www-form-urlencoded",
            stub.Captured!.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Content_type_is_matched_case_insensitively()
    {
        var (handler, stub) = Build(Ok());

        await handler.ExecuteAsync(Request(
            body: "a=1",
            headers: new Dictionary<string, string> { ["Content-Type"] = "text/plain" }));

        Assert.Equal("text/plain", stub.Captured!.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Content_type_defaults_to_json_so_existing_workflows_are_unaffected()
    {
        var (handler, stub) = Build(Ok());

        await handler.ExecuteAsync(Request(body: "{\"a\":1}"));

        Assert.Equal("application/json", stub.Captured!.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Content_type_is_not_also_copied_onto_the_request_headers()
    {
        var (handler, stub) = Build(Ok());

        await handler.ExecuteAsync(Request(
            body: "a=1",
            headers: new Dictionary<string, string>
            {
                ["content-type"] = "text/plain",
                ["accept"] = "application/json"
            }));

        // Enumerated rather than queried by name: HttpRequestHeaders.Contains throws on a content
        // header, which is the very reason the old code silently dropped it.
        Assert.DoesNotContain(stub.Captured!.Headers,
            header => string.Equals(header.Key, "content-type", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stub.Captured.Headers,
            header => string.Equals(header.Key, "accept", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_non_2xx_is_reported_rather_than_thrown()
    {
        // Throwing inside the activity would flatten to a string at the Dapr boundary and lose the
        // status and headers that on_failure rules branch on.
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("slow down", Encoding.UTF8, "text/plain")
        };
        response.Headers.Add("Retry-After", "120");

        var (handler, _) = Build(response);

        var result = await handler.ExecuteAsync(Request(method: "GET"));

        Assert.False(result.Success);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal("120", result.Headers["Retry-After"]);
        Assert.Equal("slow down", result.BodyText);
    }

    [Fact]
    public async Task Headers_are_merged_from_the_response_and_its_content()
    {
        var response = Ok();
        response.Headers.Add("x-request-id", "abc123");

        var (handler, _) = Build(response);

        var result = await handler.ExecuteAsync(Request(method: "GET"));

        Assert.Equal("abc123", result.Headers["x-request-id"]);
        Assert.Contains("application/json", result.Headers["Content-Type"]);
    }

    [Fact]
    public async Task Repeated_headers_are_joined_into_one_value()
    {
        var response = Ok();
        response.Headers.Add("x-trial", "one");
        response.Headers.Add("x-trial", "two");

        var (handler, _) = Build(response);

        var result = await handler.ExecuteAsync(Request(method: "GET"));

        Assert.Equal("one, two", result.Headers["x-trial"]);
    }

    [Fact]
    public async Task Body_text_is_captured_even_when_the_content_type_is_not_json()
    {
        // Output stays null for a non-JSON content type, which used to be indistinguishable from
        // an empty body — bodyText is what tells the two apart.
        var (handler, _) = Build(Ok("<html/>", "text/html"));

        var result = await handler.ExecuteAsync(Request(method: "GET"));

        Assert.True(result.Success);
        Assert.Null(result.Output);
        Assert.Equal("<html/>", result.BodyText);
    }

    [Fact]
    public async Task A_json_body_is_still_parsed_into_output_as_a_JsonElement()
    {
        // Deliberate, and documented in ParseJsonResponse: this is the one place in the daemon that
        // does not use WorkflowJson.Options, because a JsonElement re-serializes its original token
        // text and is the only thing preserving 10.0 as a double across the Dapr hop.
        var (handler, _) = Build(Ok("{\"name\":\"ada\"}"));

        var result = await handler.ExecuteAsync(Request(method: "GET"));

        var output = Assert.IsType<System.Text.Json.JsonElement>(result.Output);
        Assert.Equal("ada", output.GetProperty("name").GetString());
    }
}
