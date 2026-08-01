namespace Observability.Domain;

/// <summary>
/// Values of <see cref="ExecutionLogEntry.Source"/> and of the watch stream's <c>source</c> field,
/// which clients filter on. Named here because two projects produce them — the log provider when
/// a caller logged no source of its own, and the terminal frame the watch query synthesises — and
/// the filter is string equality, so a drift between the two returns nothing rather than failing.
/// </summary>
public static class FrameSource
{
    public const string System = "system";
}
