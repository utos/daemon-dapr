namespace Common.Workflow;

/// <summary>
/// The duration grammar of <c>api/docs/workflow-source-format.md</c> § Durations — <c>90s</c>,
/// <c>8h</c>, <c>1h30m</c> — as something the interpreter can call.
/// <para>
/// A port rather than a method, for one reason: the grammar is the spec's and is implemented once,
/// in <c>Utos.Workflow</c>, shared with the CLI and the shared validator so that three tools cannot
/// disagree about what <c>1h30m</c> means. That package is the wire library, which only a
/// <c>*.Api</c> project and <c>Common.Workflow.Proto</c> may reference, so the domain states what
/// it needs and the composition root supplies it — as it already does for the template engine and
/// the schema validator.
/// </para>
/// <para>
/// A literal is parsed long before this: the shared validator checks it at load
/// (<c>UTOS-C201</c>–<c>C203</c>). What reaches here is a whole-field template, which has no value
/// until the run renders it.
/// </para>
/// </summary>
public interface IDurationParser
{
    bool TryParse(string text, out TimeSpan value, out DurationFailure failure);
}

/// <summary>Why a duration string was refused. <c>None</c> means it was accepted.</summary>
public enum DurationFailure
{
    None = 0,

    /// <summary>Empty, or not the unit shorthand at all.</summary>
    Syntax,

    /// <summary>Well formed and zero, or so large it cannot be held.</summary>
    NotPositive
}
