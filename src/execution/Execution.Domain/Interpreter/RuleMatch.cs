namespace Execution.Domain.Interpreter;

/// <summary>
/// The rule that produced a <see cref="TransitionDecision"/>: which list it belongs to, where it
/// sits in that list, and how long the list is.
/// </summary>
/// <param name="List"><c>onSuccess</c>, <c>onFailure</c> or <c>onEmitted</c> — the author's own name for it.</param>
/// <param name="Number">1-based position, counted the way a reader counts rules in the file.</param>
/// <param name="Of">
/// How many rules the list holds. "rule 1 of 3" is what makes first-match-wins visible in a log:
/// the other two were never evaluated.
/// </param>
public sealed record RuleMatch(string List, int Number, int Of);
