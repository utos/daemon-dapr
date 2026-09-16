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

    // The event store's tables. Mapped here for the schema and its migration only: the stores
    // read and write them with Npgsql directly (see EventTables).
    public DbSet<StoredEvent> Events => Set<StoredEvent>();
    public DbSet<SubscriptionCheckpoint> SubscriptionCheckpoints => Set<SubscriptionCheckpoint>();
    public DbSet<ParkedEvent> ParkedEvents => Set<ParkedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkflowExecutionDbContext).Assembly);

        ConfigureEventStore(modelBuilder);

        // ExecutionContext entity configuration
        modelBuilder.Entity<ExecutionContext>(b =>
        {
            b.ToTable("ExecutionContexts");
            b.HasKey(e => e.ExecutionId);
            b.Property(e => e.ExecutionId).ValueGeneratedNever();

            // Indexed because it is queried as a set — "everything under this run" — on every
            // terminal transition, not looked up one row at a time.
            b.Property(e => e.RootExecutionId);
            b.HasIndex(e => e.RootExecutionId);

            b.Property(e => e.EntryPoint)
                .HasMaxLength(200)
                .IsRequired();

            b.Property(e => e.Bundle)
                .HasColumnName("BundleJson")
                .HasColumnType("text")
                .IsRequired()
                .HasConversion(new ValueConverter<WorkflowBundle, string>(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<WorkflowBundle>(v, JsonOptions)!));

            b.Property(e => e.Env)
                .HasColumnName("EnvJson")
                .HasColumnType("text")
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
                .HasColumnType("text")
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

    /// <summary>
    /// The event store's schema (see EventTables for why the stores bypass EF to use it). Snake
    /// case, unlike the read models, because these tables are addressed by hand-written SQL and
    /// quoted PascalCase in SQL text is a paper cut on every line.
    /// </summary>
    private static void ConfigureEventStore(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredEvent>(b =>
        {
            b.ToTable("events");
            b.HasKey(e => e.Position);
            b.Property(e => e.Position).HasColumnName("position").UseIdentityAlwaysColumn();
            b.Property(e => e.Stream).HasColumnName("stream").IsRequired();
            b.Property(e => e.Revision).HasColumnName("revision");
            b.Property(e => e.EventType).HasColumnName("event_type").IsRequired();
            b.Property(e => e.Version).HasColumnName("version");
            // `json`, not `jsonb`: jsonb normalises the document and reorders its keys, and
            // System.Text.Json's polymorphism needs the `type` discriminator first. The body is
            // stored as the serializer produced it, which is also what EventJsonShapeTests pins.
            b.Property(e => e.Data).HasColumnName("data").HasColumnType("json").IsRequired();
            b.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("json").IsRequired();
            b.Property(e => e.OccurredAt).HasColumnName("occurred_at").HasDefaultValueSql("now()");
            b.HasIndex(e => new { e.Stream, e.Revision }).IsUnique();
            // The subscription's prefix filter: `event_type like 'utos.execution.%'` uses this.
            b.HasIndex(e => e.EventType).HasOperators("text_pattern_ops");
        });

        modelBuilder.Entity<SubscriptionCheckpoint>(b =>
        {
            b.ToTable("subscription_checkpoints");
            b.HasKey(e => e.Name);
            b.Property(e => e.Name).HasColumnName("name");
            b.Property(e => e.Position).HasColumnName("position");
            b.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<ParkedEvent>(b =>
        {
            b.ToTable("parked_events");
            b.HasKey(e => new { e.Position, e.Subscription });
            b.Property(e => e.Position).HasColumnName("position");
            b.Property(e => e.Subscription).HasColumnName("subscription");
            b.Property(e => e.Stream).HasColumnName("stream").IsRequired();
            b.Property(e => e.EventType).HasColumnName("event_type").IsRequired();
            b.Property(e => e.Error).HasColumnName("error").IsRequired();
            b.Property(e => e.ParkedAt).HasColumnName("parked_at");
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
