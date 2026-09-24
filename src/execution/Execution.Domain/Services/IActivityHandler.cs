using Common.SeedWork;
using Common.Workflow;

namespace Execution.Domain.Services;

public interface IActivityHandler
{
    /// <summary>Which kind of activity this handler runs. The executor dispatches on it.</summary>
    ActivityKind Kind { get; }

    Task<ActivityHandlerResult> ExecuteAsync(ActivityHandlerInput input, CancellationToken ct = default);
}

/// <param name="RootExecutionId">
/// The root of this execution's run tree, which is what a produced blob is attached to and what its
/// history row is keyed by. The tree rather than this execution, so a blob made deep in a fan-out is
/// attached once and readable by every execution that could legitimately reach it.
/// </param>
/// <param name="Workflow">
/// The workflow this activity belongs to, with its bundle digest beside it: a name and version stop
/// identifying what ran once a bundle is rebuilt, and the digest does not.
/// </param>
public record ActivityHandlerInput(
    ExecutionId ExecutionId,
    ExecutionId LogExecutionId,
    string? Url,
    string? HttpMethod,
    string? Body,
    Dictionary<string, string>? Headers,
    // The workflow to attribute this activity's log lines to, or null for the root being watched.
    string? LogSource = null,
    ExecutionId RootExecutionId = default,
    string? Workflow = null,
    string? WorkflowDigest = null,
    string? ActivityName = null);

/// <param name="Success">
/// False when the transport succeeded but the response was not 2xx. The handler does not throw on
/// a bad status: an exception crossing the Dapr boundary flattens to a string, which would lose the
/// status and headers the failure path needs to branch on. Deciding what a non-2xx means is the
/// executor's job, not the handler's.
/// </param>
/// <param name="Headers">
/// Response headers, merged from the response and its content. Empty rather than null so the
/// <c>response</c> template context is always a well-formed object.
/// </param>
/// <param name="Body">
/// The response body as a <c>Blob</c>: the bytes exactly as the server sent the representation,
/// with the response's content type as the blob's media type. What an expression reads as
/// <c>response.body</c>.
/// <para>
/// A <c>Blob</c> rather than the bytes since spec 0.20, and inline or stored as the daemon's
/// threshold decides — indistinguishably, since nothing in an expression reveals the backing. Null
/// only when there was no response to read.
/// </para>
/// <para>
/// The <c>BodyText</c> that sat beside it is retired. It was eagerly decoded text, which cannot
/// exist for a body larger than memory, and it held a second copy of every response in history.
/// Text is now read on demand: <c>await response.body.text()</c>.
/// </para>
/// </param>
/// <param name="BodySummary">
/// The first few hundred characters of the body, decoded, <b>for a failure message only</b>.
/// <para>
/// Not the retired <c>bodyText</c> returning by another name, and the difference is the bound: that
/// was the whole body, held in history beside the bytes, and unrepresentable for a body larger than
/// memory. This is a truncated diagnostic so that "failed with status 500" still says what the
/// server actually complained about.
/// </para>
/// <para>
/// It never enters a template scope. An author reads the body with
/// <c>await response.body.text()</c>, which is bounded by the materialization limit and refuses
/// rather than truncating — because silently truncated data is worse than no data, while a
/// truncated <em>error message</em> is just an error message.
/// </para>
/// </param>
public record ActivityHandlerResult(
    int StatusCode,
    bool Success,
    object? Output,
    Dictionary<string, string> Headers,
    WorkflowBlob? Body = null,
    string? BodySummary = null);
