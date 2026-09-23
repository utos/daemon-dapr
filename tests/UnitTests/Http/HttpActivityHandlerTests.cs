using Common.Blobs;
using Common.Workflow;
using System.Net;
using System.Text;
using Common.SeedWork;
using Execution.Domain.Services;
using Execution.Infrastructure.Dapr.ActivityHandlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace UnitTests.Http;

public class HttpActivityHandlerTests
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("application/problem+json")]      // RFC 7807, how a great many APIs report an error
    [InlineData("application/vnd.api+json")]      // JSON:API
    [InlineData("APPLICATION/JSON")]              // media types are case-insensitive
    public async Task A_json_body_reaches_output_whatever_spells_json(string mediaType)
    {
        // `output` is the parsed body when the media type's essence is application/json or ends in
        // +json (binary-data.md § Reading a response). The `+json` half was missing, so a workflow
        // reading `output.detail` from a problem+json error saw null and had to re-parse the body
        // itself — which is the work `output` exists to spare it.
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"detail":"nope"}""", Encoding.UTF8, mediaType),
        };

        var (handler, _) = Build(response);

        var result = await handler.ExecuteAsync(Request());

        // A JsonElement, which is the shape that preserves the body's own number tokens across the
        // Dapr hop — see the note on ParseJsonResponse.
        var output = Assert.IsType<System.Text.Json.JsonElement>(result.Output);
        Assert.Equal("nope", output.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("application/jsonl")]             // ends in `json` but not `+json`
    public async Task A_body_that_is_not_json_leaves_output_null(string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"detail":"nope"}""", Encoding.UTF8, mediaType),
        };

        var (handler, _) = Build(response);

        Assert.Null((await handler.ExecuteAsync(Request())).Output);
    }

    [Fact]
    public async Task A_response_past_the_maximum_body_is_refused_by_name()
    {
        // UTOS-F101. Read from the stream and stopped, rather than buffered and then measured —
        // otherwise a response the daemon has already decided to refuse gets to decide how much
        // memory it takes on the way.
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[4096]),
        };

        var (handler, _) = Build(response, maxBody: 1024);

        var failure = await Assert.ThrowsAsync<ResponseTooLargeException>(
            () => handler.ExecuteAsync(Request()));

        Assert.Equal(1024, failure.Limit);
        Assert.Equal("UTOS-F101", ResponseTooLargeException.Code);
    }

    [Fact]
    public async Task A_response_at_the_maximum_body_is_read()
    {
        // The boundary is inclusive: a body that ends exactly at the limit is not past it.
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[1024]),
        };

        var (handler, _) = Build(response, maxBody: 1024);

        Assert.Equal(1024, (await handler.ExecuteAsync(Request())).Body!.Size);
    }

    [Fact]
    public async Task A_small_body_is_carried_inline()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("hello", Encoding.UTF8, "text/plain"),
        };

        var (handler, _) = Build(response, inlineThreshold: 1024);

        var body = (await handler.ExecuteAsync(Request())).Body!;

        Assert.True(body.IsInline);
        Assert.Equal("hello", Encoding.UTF8.GetString(body.Data!));
        Assert.Equal("text/plain", body.MediaType);
        Assert.Equal(5, body.Size);
    }

    [Fact]
    public async Task A_body_over_the_threshold_becomes_a_handle_and_the_bytes_go_to_the_store()
    {
        // The point of the whole change: above the threshold the value is a handle of a few dozen
        // bytes, not the payload. An inline body is copied at every hop and written to orchestration
        // history on every transition, which is durable growth rather than a memory cost.
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('x', 4096)))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf") },
            },
        };

        var stub = new StubHandler(response);
        var blobs = new FakeBlobs(threshold: 1024, canStore: true);
        var handler = new HttpActivityHandler(
            new HttpClient(stub),
            NullLogger<HttpActivityHandler>.Instance,
            new Microsoft.Extensions.Options.OptionsWrapper<HttpActivityOptions>(new HttpActivityOptions()),
            blobs);

        var body = (await handler.ExecuteAsync(Request())).Body!;

        Assert.False(body.IsInline);
        Assert.Null(body.Data);
        Assert.NotNull(body.Id);
        Assert.Equal(4096, body.Size);
        Assert.Equal("application/pdf", body.MediaType);

        // The bytes reached the store, exactly once and whole.
        Assert.Equal(4096, Assert.Single(blobs.Stored).Length);
    }

    [Fact]
    public async Task A_body_over_the_threshold_with_no_store_is_refused_by_name()
    {
        // UTOS-F102. Its own code, and not a read failure: nothing is wrong with the request or the
        // blob, the daemon simply has nowhere to put bytes — an operator's problem, not an author's.
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[4096]),
        };

        var (handler, _) = Build(response, inlineThreshold: 1024, canStore: false);

        var failure = await Assert.ThrowsAsync<BlobStoreUnavailableException>(
            () => handler.ExecuteAsync(Request()));

        Assert.Equal("UTOS-F102", BlobStoreUnavailableException.Code);
        Assert.Contains("no blob store is configured", failure.Message);
    }

    [Fact]
    public async Task A_body_exactly_at_the_threshold_stays_inline()
    {
        // At or below is inline, so the boundary is not a spill.
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[1024]),
        };

        var (handler, _) = Build(response, inlineThreshold: 1024);

        Assert.True((await handler.ExecuteAsync(Request())).Body!.IsInline);
    }

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

    private static (HttpActivityHandler Handler, StubHandler Stub) Build(
        HttpResponseMessage response,
        long? maxBody = null,
        long inlineThreshold = long.MaxValue,
        bool canStore = true)
    {
        var stub = new StubHandler(response);

        var options = new HttpActivityOptions();
        if (maxBody is { } limit) options.MaxResponseBodyBytes = limit;

        var handler = new HttpActivityHandler(
            new HttpClient(stub),
            NullLogger<HttpActivityHandler>.Instance,
            new Microsoft.Extensions.Options.OptionsWrapper<HttpActivityOptions>(options),
            new FakeBlobs(inlineThreshold, canStore));

        return (handler, stub);
    }

    /// <summary>
    /// Blob access with no store behind it: it records what it was asked to store and hands back a
    /// handle, which is all the handler's decision needs. What a real store does with the bytes is
    /// <c>S3ObjectStoreTests</c>' subject, and the round trip through a real one is the integration
    /// suite's.
    /// </summary>
    private sealed class FakeBlobs(long threshold, bool canStore) : IBlobAccess
    {
        public long InlineThreshold => threshold;

        public bool CanStore => canStore;

        public List<byte[]> Stored { get; } = [];

        public Task<WorkflowBlob> ProduceAsync(
            Stream content, string mediaType, string? name, BlobUsage provenance, CancellationToken ct)
        {
            var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var bytes = buffer.ToArray();
            Stored.Add(bytes);

            return Task.FromResult(
                WorkflowBlob.Stored($"b_{Stored.Count}", bytes.Length, mediaType, name: name));
        }

        public Task<byte[]> ReadAsync(WorkflowBlob blob, ExecutionId runTree, CancellationToken ct) =>
            Task.FromResult(blob.Data ?? []);
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
        // The bounded diagnostic, not the body: an author reads the body with `await
        // response.body.text()`, and this is only what a failure message says.
        Assert.Equal("slow down", result.BodySummary);
        Assert.Equal("slow down", Encoding.UTF8.GetString(result.Body!.Data!));
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
        Assert.Equal("<html/>", Encoding.UTF8.GetString(result.Body!.Data!));
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
