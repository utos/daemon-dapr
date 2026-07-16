using Microsoft.Extensions.Logging;

namespace Observability.Domain;

public record ExecutionLogEntry(
    DateTime Timestamp,
    LogLevel Level,
    string Message,
    string Source,
    string Category);
