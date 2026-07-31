namespace Observability.Application.Models;

/// <summary>
/// Severity as a watcher sees it. Coarser than <see cref="Microsoft.Extensions.Logging.LogLevel"/>
/// — Trace collapses into Debug and Critical into Error — because that is the granularity the
/// watch API exposes.
///
/// The ordinals are load-bearing: a level filter is an ordering comparison
/// (<c>frame.Level &lt; minimum</c>), and they must stay aligned with the wire enum they are
/// mapped to in <c>Observability.Api</c>.
/// </summary>
public enum ExecutionFrameLevel
{
    Unspecified = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4
}
