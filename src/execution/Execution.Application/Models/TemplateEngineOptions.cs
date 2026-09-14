namespace Execution.Application.Models;

/// <summary>
/// The limits one expression evaluation runs under (<c>api/docs/template-expressions.md</c>
/// § Runtime guarantees). The spec requires that each exists and is reported with its own code;
/// the values are the daemon's.
/// <para>
/// The statement budget counts callback invocations as well as statements: a 5,000-record
/// flatten over a history response needs 10–20k, so the default leaves an order of magnitude
/// of headroom for ordinary use while still cutting a runaway off in milliseconds.
/// </para>
/// </summary>
public class TemplateEngineOptions
{
    public const string SectionName = "Expressions";

    public int TimeoutMilliseconds { get; set; } = 1_000;

    /// <summary>Statements plus callback invocations per evaluation. Zero disables the budget.</summary>
    public int MaxStatements { get; set; } = 100_000;

    /// <summary>Sampled between operations, so a single native call can overshoot it.</summary>
    public long MemoryLimitBytes { get; set; } = 64L * 1024 * 1024;

    public int MaxRecursionDepth { get; set; } = 64;

    public uint MaxArraySize { get; set; } = 1_000_000;

    public int MaxJsonParseDepth { get; set; } = 64;

    /// <summary>Applied both to regex literals compiled at parse time and to matches at run time.</summary>
    public int RegexTimeoutMilliseconds { get; set; } = 100;

    public int MaxResultDepth { get; set; } = 200;

    public int MaxResultNodes { get; set; } = 1_000_000;
}
