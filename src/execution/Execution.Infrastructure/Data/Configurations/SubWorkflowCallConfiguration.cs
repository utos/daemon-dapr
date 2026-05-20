using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Execution.Infrastructure.ReadModels;

namespace Execution.Infrastructure.Data.Configurations;

public class SubWorkflowCallConfiguration : IEntityTypeConfiguration<SubWorkflowCall>
{
    public void Configure(EntityTypeBuilder<SubWorkflowCall> builder)
    {
        builder.ToTable("SubWorkflowCalls");

        builder.HasKey(e => new { e.ParentExecutionId, e.ChildExecutionId });

        builder.Property(e => e.ActivityName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(e => e.InvocationType)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(e => e.PromiseMode)
            .HasMaxLength(20);

        builder.Property(e => e.BranchName)
            .HasMaxLength(200);

        builder.HasIndex(e => e.ChildExecutionId);
        builder.HasIndex(e => new { e.ParentExecutionId, e.ActivityName });
    }
}
