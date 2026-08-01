using System.Text.Json;
using Common.Workflow.Serialization;
using Common.SeedWork;
using Common.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Execution.Infrastructure.ReadModels;
using ExecutionContext = Execution.Domain.Services.ExecutionContext;

namespace Execution.Infrastructure.Data;

public class WorkflowExecutionDbContext(DbContextOptions<WorkflowExecutionDbContext> options) : DbContext(options)
{

    // The shared instance, not a local copy: three of these existed and two paths passed none
    // at all, which is how the same value came back a real CLR type down one route and a
    // JsonElement down another.
    private static JsonSerializerOptions JsonOptions => WorkflowJson.Options;

    public DbSet<WorkflowExecutionReadModel> WorkflowExecutions => Set<WorkflowExecutionReadModel>();
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
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<WorkflowBundle>(v, JsonOptions)!));

            b.Property(e => e.Env)
                .HasColumnName("EnvJson")
                .HasColumnType("nvarchar(max)")
                .IsRequired()
                .HasConversion(
                    new ValueConverter<Dictionary<string, string>, string>(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<Dictionary<string, string>>(
                            v, (JsonSerializerOptions?)null)!),
                    new ValueComparer<Dictionary<string, string>>(
                        (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null)
                                  == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
                        v => JsonSerializer.Deserialize<Dictionary<string, string>>(
                            JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                            (JsonSerializerOptions?)null)!));

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

        // Registered as a convention rather than per property, so every execution id — the read
        // model's key, ParentExecutionId, and ExecutionContext's key — keeps the uniqueidentifier
        // column it already had without each one restating the conversion.
        configurationBuilder.Properties<ExecutionId>()
            .HaveConversion<ExecutionIdValueConverter>();
    }
}
