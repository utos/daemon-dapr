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

    /// <summary>
    /// The most bytes a single <c>bytes()</c> or <c>text()</c> call, or a single <c>Blob</c> or
    /// <c>File</c> constructor, may bring into an expression's memory. Past it, <c>UTOS-E111</c>.
    /// <para>
    /// <b>The spec sets a floor of 1 MiB and this must not go below it</b>
    /// (<c>api/docs/binary-data.md</c> § Limits). It is the one portability floor in the design: a
    /// document that verifies a webhook signature reads the body in an expression, and it has to
    /// work on every daemon. Above the floor the ceiling is a deployment's choice, which is why
    /// this defaults well above it.
    /// </para>
    /// </summary>
    public long MaterializationLimitBytes { get; set; } = 16L * 1024 * 1024;

    public int MaxJsonParseDepth { get; set; } = 64;

    /// <summary>Applied both to regex literals compiled at parse time and to matches at run time.</summary>
    public int RegexTimeoutMilliseconds { get; set; } = 100;

    public int MaxResultDepth { get; set; } = 200;

    public int MaxResultNodes { get; set; } = 1_000_000;
}
