using Common.SeedWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Common.Infrastructure.Data;

public abstract class BaseConfiguration<TEntity, TId> : IEntityTypeConfiguration<TEntity>
    where TEntity : Entity<TId>
    where TId : notnull
{
    public virtual void Configure(EntityTypeBuilder<TEntity> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasDefaultValueSql("gen_random_uuid()")
            .ValueGeneratedOnAdd();

        builder.Property(e => e.CreatedAt)
            .IsRequired();

        builder.Property(e => e.LastModifiedAt);

        // Not a row version any more: that was SQL Server's `rowversion`, which Postgres has no
        // byte[] equivalent of. The read models have one writer, the projection, so an optimistic
        // token guarded nothing; the property stays on the entity and out of the table.
        builder.Ignore(e => e.Version);
    }
}
