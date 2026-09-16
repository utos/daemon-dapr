using System.Text.Json.Serialization;

namespace Common.Workflow;

/// <summary>
/// How a promise activity decides it is done.
/// <para>
/// Persisted as a name rather than an ordinal: this reaches the event stream inside an activity
/// config, and an ordinal would silently change meaning if a member were ever inserted.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PromiseMode>))]
public enum PromiseMode
{
    /// <summary>Every branch must succeed; all results are returned.</summary>
    All,

    /// <summary>The first branch to succeed wins; failures are tolerated until none are left.</summary>
    Any,

    /// <summary>The first branch to settle wins, whether it succeeded or failed.</summary>
    Race,

    /// <summary>The first <c>required_count</c> branches to succeed win.</summary>
    Count
}

// The `PromiseModes` string table that parsed a mode name is gone. The wire format models
// completion as a oneof, so an unknown mode is no longer representable and there is nothing left
// to parse — mapping selects the member from the oneof case directly, and the spec retired the
// rule (UTOS-C301) that used to check it.
