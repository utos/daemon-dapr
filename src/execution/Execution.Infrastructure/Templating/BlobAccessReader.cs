using Common.Blobs;
using Common.SeedWork;
using Common.Workflow;
using Execution.Domain.Templating;
using Microsoft.Extensions.DependencyInjection;

namespace Execution.Infrastructure.Templating;

/// <summary>
/// <see cref="IBlobReader"/> over <see cref="IBlobAccess"/>: what the expression engine uses to read
/// a stored blob's bytes.
/// <para>
/// Two things this adapter does, and both are why it exists rather than the engine holding
/// <c>IBlobAccess</c> directly. It supplies the <b>run tree</b>, which the engine has no business
/// knowing about and which the scope carries — so the access rule is enforced on every read without
/// the language gaining a concept of run trees. And it bridges async to sync, because
/// <c>template-expressions.md</c> § Awaiting requires a read to have happened by the time the member
/// returns: the promise it hands back is already settled, which is what makes an expression's reads
/// happen in program order whatever it awaits, and makes a replay read the same bytes in the same
/// order.
/// </para>
/// <para>
/// Blocking is safe here and nowhere near an orchestrator: expressions are evaluated inside a Dapr
/// <em>activity</em>, whose whole job is to do the waiting so the orchestration can replay without
/// it.
/// </para>
/// </summary>
/// <para>
/// A scope per read, rather than one <see cref="IBlobAccess"/> held for the reader's life. The
/// template engine is a singleton — it caches one Jint engine per template scope — while blob access
/// is scoped, because it reads the daemon's own database. Capturing a scoped service in a singleton
/// is the bug that produces a <c>DbContext</c> used from two threads at once, so the scope is opened
/// here, per read, and closed with it.
/// </para>
public sealed class BlobAccessReader(IServiceScopeFactory scopes) : IBlobReader
{
    public byte[] Read(WorkflowBlob blob, ExecutionId runTree)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var blobs = scope.ServiceProvider.GetRequiredService<IBlobAccess>();

            return blobs.ReadAsync(blob, runTree, CancellationToken.None)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Common.Blobs.BlobUnreadableException unreadable)
        {
            // Same code either way (UTOS-F103). The engine's exception type is the domain's, so the
            // contract's is translated rather than leaked through the language.
            throw new Execution.Domain.Templating.BlobUnreadableException(
                unreadable.Message, unreadable);
        }
    }
}
