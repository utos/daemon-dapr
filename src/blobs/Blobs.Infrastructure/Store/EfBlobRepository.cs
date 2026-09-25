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

    public async Task<IReadOnlySet<BlobId>> MembersOfRunTreeAsync(
        IReadOnlyCollection<BlobId> ids, ExecutionId rootExecution, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return new HashSet<BlobId>();

        var values = ids.Select(id => id.Value).Distinct().ToArray();

        var members = await context.BlobRunTrees
            .AsNoTracking()
            .Where(t => t.RootExecutionId == rootExecution.Value && values.Contains(t.BlobId))
            .Select(t => t.BlobId)
            .ToListAsync(cancellationToken);

        return members.Select(value => new BlobId(value)).ToHashSet();
    }

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


    public Task<int> RecordRunTreeEndedAsync(
        ExecutionId rootExecution,
        RunTreeOutcome outcome,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var name = outcome.ToString();

        // One UPDATE, no entities loaded. This runs inside the execution module's event subscription,
        // on the same await chain: a listener that blocks stalls the subscription, so the small thing
        // is the only thing it may do.
        //
        // Conditional on the ending being unset so that a replayed or re-delivered lifecycle event
        // cannot move a recorded ending forward — the failed-run window is measured from it, and a
        // re-delivery would silently restart the clock.
        return context.BlobRunTrees
            .Where(t => t.RootExecutionId == rootExecution.Value && t.TreeEndedAt == null)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(t => t.TreeEndedAt, at)
                    .SetProperty(t => t.TreeOutcome, name),
                cancellationToken);
    }

    public async Task<IReadOnlyList<StoredBlob>> FindReclaimableAsync(
        BlobRetentionPolicy policy,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        var youngest = now - policy.GracePeriod;
        var abandoned = now - policy.AbandonedUploadWindow;
        var failedBefore = now - policy.FailedRunWindow;
        var interruptedBefore = now - policy.InterruptedRunWindow;

        var pending = BlobState.Pending.ToString();
        var ready = BlobState.Ready.ToString();
        var intermediate = BlobRetention.Intermediate.ToString();
        var completed = RunTreeOutcome.Completed.ToString();

        var rows = await context.Blobs
            .AsNoTracking()
            // Never something this new: producing a blob is three writes, and between the first two
            // a real blob has no attachment and is indistinguishable from one nothing can reach.
            .Where(b => b.CreatedAt < youngest)
            .Where(b =>
                // An upload created and never completed. Durable by class — somebody asked for it —
                // but there is nothing to keep, and the class exists to stop the daemon deleting
                // *bytes* a client is relying on.
                (b.State == pending && b.CreatedAt < abandoned)

                // Or an intermediate that no live tree can reach and no window still covers. Durable
                // blobs are absent from this clause entirely: they are removed only on request.
                || (b.State == ready
                    && b.Retention == intermediate
                    && !context.BlobRunTrees.Any(t =>
                        t.BlobId == b.Id
                        && (
                            // A tree that has not ended can still read it — including on a retry,
                            // which re-runs an activity from its recorded input. This is the spec's
                            // one hard requirement, and the backstop is what stops it being forever.
                            (t.TreeEndedAt == null && t.AttachedAt >= interruptedBefore)

                            // A failed or cancelled tree's intermediates are the evidence. Kept until
                            // the window passes, and a completed tree's are not kept at all.
                            || (t.TreeOutcome != completed && t.TreeEndedAt >= failedBefore)))))
            .OrderBy(b => b.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<RunTreeBlob>> FindRunTreeBlobsAsync(
        ExecutionId rootExecution, CancellationToken cancellationToken)
    {
        var root = rootExecution.Value;

        var rows = await (
            from attachment in context.BlobRunTrees.AsNoTracking()
            where attachment.RootExecutionId == root
            join blob in context.Blobs.AsNoTracking() on attachment.BlobId equals blob.Id
            select new
            {
                Blob = blob,
                ReachableElsewhere = context.BlobRunTrees.Any(other =>
                    other.BlobId == blob.Id
                    && other.RootExecutionId != root
                    && other.TreeEndedAt == null),
            }).ToListAsync(cancellationToken);

        return rows.Select(row => new RunTreeBlob(ToDomain(row.Blob), row.ReachableElsewhere)).ToList();
    }

    public Task DetachRunTreeAsync(ExecutionId rootExecution, CancellationToken cancellationToken) =>
        context.BlobRunTrees
            .Where(t => t.RootExecutionId == rootExecution.Value)
            .ExecuteDeleteAsync(cancellationToken);

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
