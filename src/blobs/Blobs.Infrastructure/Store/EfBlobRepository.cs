using Blobs.Domain;
using Blobs.Infrastructure.Data;
using Common.Blobs;
using Common.SeedWork;
using Microsoft.EntityFrameworkCore;

namespace Blobs.Infrastructure.Store;

/// <summary>
/// <see cref="IBlobRepository"/> over the module's tables.
/// <para>
/// The mapping between the domain record and the rows is written out rather than generated,
/// because the enums are persisted as strings on purpose (see <see cref="BlobEntity"/>) and a
/// convention that silently fell back to ordinals is the failure this is guarding against.
/// </para>
/// </summary>
public sealed class EfBlobRepository(BlobsDbContext context) : IBlobRepository
{
    public async Task<StoredBlob?> FindAsync(BlobId id, CancellationToken cancellationToken)
    {
        var row = await context.Blobs
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id.Value, cancellationToken);

        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyDictionary<BlobId, StoredBlob>> FindManyAsync(
        IReadOnlyCollection<BlobId> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return new Dictionary<BlobId, StoredBlob>();

        var values = ids.Select(id => id.Value).Distinct().ToArray();

        var rows = await context.Blobs
            .AsNoTracking()
            .Where(b => values.Contains(b.Id))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => new BlobId(row.Id), ToDomain);
    }

    public async Task AddAsync(StoredBlob blob, CancellationToken cancellationToken)
    {
        context.Blobs.Add(ToEntity(blob));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(StoredBlob blob, CancellationToken cancellationToken)
    {
        var row = await context.Blobs.FirstOrDefaultAsync(b => b.Id == blob.Id.Value, cancellationToken)
                  ?? throw new InvalidOperationException($"No blob '{blob.Id}'.");

        row.State = blob.State.ToString();
        row.Retention = blob.Retention.ToString();
        row.MediaType = blob.MediaType;
        row.Size = blob.Size;
        row.Name = blob.Name;
        row.LastModified = blob.LastModified;

        await context.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> BelongsToRunTreeAsync(
        BlobId id, ExecutionId rootExecution, CancellationToken cancellationToken) =>
        context.BlobRunTrees
            .AsNoTracking()
            .AnyAsync(
                t => t.BlobId == id.Value && t.RootExecutionId == rootExecution.Value,
                cancellationToken);

    public async Task AttachToRunTreeAsync(
        BlobId id, ExecutionId rootExecution, CancellationToken cancellationToken)
    {
        // Asked first, and the database constraint is still the thing that makes this correct.
        //
        // The check alone would be a check-then-insert that two writers could both pass; the
        // constraint alone is not enough either, because EF's change tracker refuses a second
        // entity with the same key *before* any SQL is sent — so a repeat through one context
        // threw InvalidOperationException rather than the DbUpdateException below, and "attaching
        // twice is a no-op" was not true at all.
        if (await BelongsToRunTreeAsync(id, rootExecution, cancellationToken))
            return;

        context.BlobRunTrees.Add(new BlobRunTreeEntity
        {
            BlobId = id.Value,
            RootExecutionId = rootExecution.Value,
            AttachedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Already a member, or another writer won the race. Attaching twice is meant to be a
            // no-op, and the pair being the primary key is what makes that true under concurrency:
            // two activities of one tree can both attach the same blob and exactly one insert wins.
            //
            // Confirmed rather than assumed — a DbUpdateException that is *not* this would
            // otherwise be swallowed, and a blob silently unattached fails much later as
            // "does not belong to this run tree", which reads like an access bug.
            context.ChangeTracker.Clear();

            if (!await BelongsToRunTreeAsync(id, rootExecution, cancellationToken))
                throw;
        }
    }

    public async Task RecordEventAsync(
        BlobId id,
        BlobEventKind kind,
        DateTimeOffset at,
        BlobUsage? usage,
        CancellationToken cancellationToken)
    {
        var kindName = kind.ToString();
        var root = usage?.RootExecution.Value;
        var workflow = usage?.Workflow;

        var existing = await context.BlobHistory.FirstOrDefaultAsync(
            e => e.BlobId == id.Value
                 && e.Kind == kindName
                 && e.RootExecutionId == root
                 && e.Workflow == workflow,
            cancellationToken);

        if (existing is not null)
        {
            // Fold in. `Executions` counts distinct executions, and this cannot tell whether the
            // caller is a new one without keeping the ids it is designed not to keep — so it counts
            // the occurrences it is told about, and the one place that knows better (a read
            // repeated inside one execution) is expected not to call twice per execution.
            if (at > existing.LastAt) existing.LastAt = at;
            if (at < existing.FirstAt) existing.FirstAt = at;
            existing.Executions++;

            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        context.BlobHistory.Add(new BlobHistoryEntity
        {
            BlobId = id.Value,
            Kind = kindName,
            RootExecutionId = root,
            Workflow = workflow,
            WorkflowDigest = usage?.WorkflowDigest,
            Activity = usage?.Activity,
            FirstAt = at,
            LastAt = at,
            Executions = 1,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another writer inserted the same row between the read and the write. The unique index
            // is what turns that race into a retry rather than a duplicated row, and a second pass
            // finds the row and folds into it.
            context.ChangeTracker.Clear();
            await RecordEventAsync(id, kind, at, usage, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<BlobHistoryEntry>> HistoryAsync(
        BlobId id, CancellationToken cancellationToken)
    {
        var rows = await context.BlobHistory
            .AsNoTracking()
            .Where(e => e.BlobId == id.Value)
            .OrderBy(e => e.FirstAt)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new BlobHistoryEntry(
            Enum.Parse<BlobEventKind>(row.Kind),
            row.FirstAt,
            row.LastAt,
            row.RootExecutionId is { } root ? new ExecutionId(root) : null,
            row.Workflow,
            row.WorkflowDigest,
            row.Activity,
            row.Executions)).ToList();
    }


    private static StoredBlob ToDomain(BlobEntity row) =>
        new(new BlobId(row.Id),
            new ObjectKey(row.ObjectKey),
            Enum.Parse<BlobState>(row.State),
            Enum.Parse<BlobRetention>(row.Retention),
            Enum.Parse<BlobOrigin>(row.Origin),
            row.MediaType,
            row.Size,
            row.CreatedAt,
            row.Name,
            row.LastModified);

    private static BlobEntity ToEntity(StoredBlob blob) =>
        new()
        {
            Id = blob.Id.Value,
            ObjectKey = blob.Key.Value,
            State = blob.State.ToString(),
            Retention = blob.Retention.ToString(),
            Origin = blob.Origin.ToString(),
            MediaType = blob.MediaType,
            Size = blob.Size,
            Name = blob.Name,
            LastModified = blob.LastModified,
            CreatedAt = blob.CreatedAt,
        };
}
