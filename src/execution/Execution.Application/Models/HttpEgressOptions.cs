namespace Execution.Application.Models;

/// <summary>
/// Policy for outbound HTTP made by workflow activities. This is the daemon's own egress
/// posture, not something a workflow document can influence — a workflow declares a URL, and
/// whether that URL may be reached is decided here.
///
/// Bound at startup rather than captured per-run: unlike <see cref="ExecutionLimitsOptions"/>,
/// egress policy is not replayed. It is enforced inside an activity, so a run that was allowed
/// to reach a host yesterday may legitimately be refused today without breaking replay
/// determinism — the activity's recorded result is what replays, not the decision.
/// </summary>
public class HttpEgressOptions
{
    public const string SectionName = "Execution:HttpEgress";

    /// <summary>
    /// How long a single workflow HTTP request may take. Deliberately not the default
    /// <see cref="HttpClient"/> 100 s: an activity that hangs holds an orchestration slot, and
    /// there is no retry to make a short timeout costly.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether workflows may reach loopback addresses (127.0.0.0/8, ::1).
    /// Allowed by default: a daemon calling a sidecar on localhost is a normal deployment.
    /// </summary>
    public bool AllowLoopback { get; set; } = true;

    /// <summary>
    /// Whether workflows may reach private ranges (10/8, 172.16/12, 192.168/16, fc00::/7).
    /// Allowed by default: denying them would break every self-hosted daemon calling an
    /// internal API, which is the common case rather than the exception.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; } = true;

    // There is deliberately no option for link-local (169.254.0.0/16, fe80::/10). It is always
    // denied: it is the cloud instance-metadata range, so reaching it turns "can author a
    // workflow" into "holds the cloud account's credentials". Making it configurable would put
    // that escalation one config mistake away.
}
