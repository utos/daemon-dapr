using Common.SeedWork;
using Common.Workflow;

namespace Execution.Domain.Services;

public interface IActivityHandler
{
    /// <summary>Which kind of activity this handler runs. The executor dispatches on it.</summary>
    ActivityKind Kind { get; }

    Task<ActivityHandlerResult> ExecuteAsync(ActivityHandlerInput input, CancellationToken ct = default);
}

public record ActivityHandlerInput(
    ExecutionId ExecutionId,
    ExecutionId LogExecutionId,
    string? Url,
    string? HttpMethod,
    string? Body,
    Dictionary<string, string>? Headers,
    // The workflow to attribute this activity's log lines to, or null for the root being watched.
    string? LogSource = null);

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
/// <param name="BodyText">
/// The response body as text. Always populated, whatever the content type — <paramref name="Output"/>
/// is null unless the response was JSON, and without the text a wrong content type looks
/// identical to an empty body.
/// </param>
/// <param name="Body">
/// The response body as it arrived, before any decoding: what an expression reads as
/// <c>response.body</c>, a <c>Buffer</c>. The only route by which a binary body, or one in an
/// encoding the text decoding guessed wrong, reaches a workflow intact.
/// </param>
public record ActivityHandlerResult(
    int StatusCode,
    bool Success,
    object? Output,
    Dictionary<string, string> Headers,
    string? BodyText,
    byte[]? Body = null);
