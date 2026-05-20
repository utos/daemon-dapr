using System.Text.Json;
using Common.Infrastructure.Serialization;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Utos.Workflow.V1;
using Execution.Infrastructure.ReadModels;
using ExecutionContext = Execution.Domain.Services.ExecutionContext;

namespace Execution.Infrastructure.Data;

public class WorkflowExecutionDbContext(DbContextOptions<WorkflowExecutionDbContext> options) : DbContext(options)
{
    private static readonly JsonParser ProtoParser = new(JsonParser.Settings.Default);
    private static readonly JsonFormatter ProtoFormatter = new(JsonFormatter.Settings.Default);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new ObjectDictionaryJsonConverter() }
    };

    public DbSet<WorkflowExecutionReadModel> WorkflowExecutions => Set<WorkflowExecutionReadModel>();
    public DbSet<SubWorkflowCall> SubWorkflowCalls => Set<SubWorkflowCall>();
    public DbSet<ExecutionContext> ExecutionContexts => Set<ExecutionContext>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkflowExecutionDbContext).Assembly);

        // ExecutionContext entity configuration
        modelBuilder.Entity<ExecutionContext>(b =>
        {
            b.ToTable("ExecutionContexts");
            b.HasKey(e => e.ExecutionId);
            b.Property(e => e.ExecutionId).ValueGeneratedNever();

            b.Property(e => e.EntryPoint)
                .HasMaxLength(200)
                .IsRequired();

            b.Property(e => e.Bundle)
                .HasColumnName("BundleJson")
                .HasColumnType("nvarchar(max)")
                .IsRequired()
                .HasConversion(new ValueConverter<WorkflowBundle, string>(
                    v => ProtoFormatter.Format(v),
                    v => ProtoParser.Parse<WorkflowBundle>(v)));

            b.Property(e => e.Input)
                .HasColumnName("InputJson")
                .HasColumnType("nvarchar(max)")
                .IsRequired()
                .HasConversion(
                    new ValueConverter<Dictionary<string, object>, string>(
                        v => JsonSerializer.Serialize(v, JsonOptions),
                        v => JsonSerializer.Deserialize<Dictionary<string, object>>(v, JsonOptions)!),
                    new ValueComparer<Dictionary<string, object>>(
                        (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
                        v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
                        v => JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!));
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Configure all DateTime properties to be stored and retrieved as UTC
        configurationBuilder.Properties<DateTime>()
            .HaveConversion<UtcDateTimeConverter>();

        configurationBuilder.Properties<DateTime?>()
            .HaveConversion<UtcNullableDateTimeConverter>();
    }
}
