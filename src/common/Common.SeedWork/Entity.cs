namespace Common.SeedWork;

public abstract class Entity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public byte[] Version { get; set; } = null!;
}
