using Common.Infrastructure.Data;
using Common.SeedWork;
using Common.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Execution.Infrastructure.ReadModels;

namespace Execution.Infrastructure.Data.Configurations;

public class WorkflowExecutionReadModelConfiguration : BaseConfiguration<WorkflowExecutionReadModel, ExecutionId>
{
    public override void Configure(EntityTypeBuilder<WorkflowExecutionReadModel> builder)
    {
        base.Configure(builder); // Apply base configuration (Id, timestamps, version)

        builder.ToTable("WorkflowExecutions");

        // Parent execution ID (null for top-level, set for sub-workflows)
        builder.Property(e => e.ParentExecutionId);
        builder.HasIndex(e => e.ParentExecutionId);

        // Status column
        builder.Property(e => e.Status)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(e => e.Status); // Index for filtering by status

        // Workflow identification. Owned rather than a JSON blob or a formatted string: the
        // segments stay first-class columns, so the existing indexes and the ListExecutions
        // filter keep working. Column names are pinned to what they were before the identity
        // became a type, so this mapping needs no migration.
        builder.OwnsOne(e => e.Identity, identity =>
        {
            identity.Property(i => i.Name)
                .HasColumnName("WorkflowName")
                .HasMaxLength(200)
                .IsRequired();

            identity.Property(i => i.Version)
                .HasColumnName("WorkflowVersion")
                .HasMaxLength(50)
                .HasConversion(
                    version => version.ToString(),
                    text => SemanticVersion.Parse(text))
                .IsRequired();

            identity.Property(i => i.Namespace)
                .HasColumnName("WorkflowNamespace")
                .HasMaxLength(200);

            identity.Property(i => i.Registry)
                .HasColumnName("WorkflowRegistry")
                .HasMaxLength(200);

            identity.HasIndex(i => new { i.Name, i.Version }); // Composite index
        });

        builder.Navigation(e => e.Identity).IsRequired();

        builder.Property(e => e.WorkflowDigest)
            .HasMaxLength(100);

        builder.HasIndex(e => e.WorkflowDigest); // Digest filter is highly selective

        // Timing columns
        builder.Property(e => e.ScheduledAt);
        builder.Property(e => e.StartedAt);
        builder.Property(e => e.CompletedAt);
        builder.Property(e => e.FailedAt);

        builder.HasIndex(e => e.CreatedAt); // Index for sorting by creation time

        // Error information
        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(4000); // SQL Server max for nvarchar without max

        builder.Property(e => e.ErrorCode)
            .HasMaxLength(200);

        // JSON columns - store as text
        builder.Property(e => e.WorkflowDefinitionJson)
            .HasColumnType("nvarchar(max)"); // SQL Server: nvarchar(max), PostgreSQL will use text

        builder.Property(e => e.InputJson)
            .HasColumnType("nvarchar(max)");

        builder.Property(e => e.EnvJson)
            .HasColumnType("nvarchar(max)");
    }
}
