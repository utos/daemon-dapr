using Common.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Execution.Infrastructure.ReadModels;

namespace Execution.Infrastructure.Data.Configurations;

public class WorkflowExecutionReadModelConfiguration : BaseConfiguration<WorkflowExecutionReadModel>
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

        // Workflow identification
        builder.Property(e => e.WorkflowName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(e => e.WorkflowVersion)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(e => new { e.WorkflowName, e.WorkflowVersion }); // Composite index

        // Timing columns
        builder.Property(e => e.ScheduledAt);
        builder.Property(e => e.StartedAt);
        builder.Property(e => e.CompletedAt);
        builder.Property(e => e.FailedAt);

        builder.HasIndex(e => e.CreatedAt); // Index for sorting by creation time

        // Error information
        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(4000); // SQL Server max for nvarchar without max

        // JSON columns - store as text
        builder.Property(e => e.WorkflowDefinitionJson)
            .HasColumnType("nvarchar(max)"); // SQL Server: nvarchar(max), PostgreSQL will use text

        builder.Property(e => e.InputJson)
            .HasColumnType("nvarchar(max)");
    }
}
