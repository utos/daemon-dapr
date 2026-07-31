using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Common.SeedWork;

/// <summary>
/// Identifies one workflow execution. A wrapper rather than a bare <see cref="Guid"/> so an
/// execution id cannot be passed where some other id is expected — the Dapr activity inputs alone
/// carry several ids side by side, including an execution's own and the one its logs are routed
/// under.
///
/// The converter is declared on the type, following <c>ValueMap</c>, so the persisted form stays
/// the bare GUID string a raw <see cref="Guid"/> wrote. That matters more here than elsewhere:
/// these ids sit in every event in the stream, and the event store logs and drops an event it
/// cannot deserialize rather than failing, so a changed shape would quietly truncate history
/// instead of erroring.
/// </summary>
[JsonConverter(typeof(ExecutionIdJsonConverter))]
public readonly record struct ExecutionId(Guid Value)
{
    public static ExecutionId New() => new(Guid.NewGuid());

    public static ExecutionId Parse(string id) =>
        TryParse(id, out var parsed)
            ? parsed
            : throw new FormatException($"'{id}' is not a valid execution id.");

    public static bool TryParse([NotNullWhen(true)] string? id, out ExecutionId parsed)
    {
        if (Guid.TryParse(id, out var guid))
        {
            parsed = new ExecutionId(guid);
            return true;
        }

        parsed = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}
