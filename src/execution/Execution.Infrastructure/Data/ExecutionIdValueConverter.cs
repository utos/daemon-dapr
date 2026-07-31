using Common.SeedWork;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Execution.Infrastructure.Data;

/// <summary>
/// Stores an <see cref="ExecutionId"/> as the <c>uniqueidentifier</c> it was before it became a
/// type, so no column changes and existing rows keep reading.
/// </summary>
public sealed class ExecutionIdValueConverter() : ValueConverter<ExecutionId, Guid>(
    id => id.Value,
    value => new ExecutionId(value));
