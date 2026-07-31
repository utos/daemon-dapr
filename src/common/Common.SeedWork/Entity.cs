namespace Common.SeedWork;

/// <summary>
/// The audit surface every persisted entity carries, split out from <see cref="Entity{TId}"/>
/// so change-tracking interceptors can stamp it without knowing the entity's id type.
/// </summary>
public abstract class Entity
{
    public DateTime CreatedAt { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public byte[] Version { get; set; } = null!;
}

public abstract class Entity<TId> : Entity where TId : notnull
{
    public TId Id { get; init; } = default!;
}
