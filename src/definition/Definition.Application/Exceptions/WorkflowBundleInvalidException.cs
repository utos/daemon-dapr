namespace Definition.Application.Exceptions;

/// <summary>
/// Thrown when a pushed bundle fails validation. Mapped to gRPC <c>INVALID_ARGUMENT</c> at the
/// service boundary; the message is the validation result verbatim, so the CLI still reports
/// every rule that failed.
/// </summary>
public class WorkflowBundleInvalidException(string validationMessage) : Exception(validationMessage);
