using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Execution.Infrastructure.Data;

/// <summary>
/// Converts DateTime values to UTC when reading from database.
/// Ensures all DateTime values have DateTimeKind.Utc set.
/// </summary>
public class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(
            // When writing to database: ensure UTC
            v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc),
            // When reading from database: specify as UTC
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}

/// <summary>
/// Converts nullable DateTime values to UTC when reading from database.
/// </summary>
public class UtcNullableDateTimeConverter : ValueConverter<DateTime?, DateTime?>
{
    public UtcNullableDateTimeConverter()
        : base(
            // When writing to database: ensure UTC
            v => v.HasValue
                ? v.Value.Kind == DateTimeKind.Utc ? v.Value : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)
                : (DateTime?)null,
            // When reading from database: specify as UTC
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : (DateTime?)null)
    {
    }
}
