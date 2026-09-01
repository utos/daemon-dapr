using System.Security.Cryptography;
using System.Text;
using Common.SeedWork;

namespace Execution.Infrastructure.Dapr;

/// <summary>
/// An execution id derived from durable state rather than drawn from the orchestrator's GUID
/// sequence.
/// <para>
/// <c>WorkflowContext.NewGuid</c> is replay-safe but <em>not</em> continuation-safe: its counter
/// restarts with the history, so the first id minted after a <c>ContinueAsNew</c> is the same one
/// the first id of the previous segment was. That is harmless when the earlier child has finished
/// and fatal when it has not — a consumer's producer survives every continuation by design, so
/// the second segment's first child collides with a live instance, Dapr declines to start it, and
/// the caller waits on a rendezvous that can never happen. It fails silently: no exception
/// reaches the orchestrator, the child simply never runs.
/// </para>
/// <para>
/// Deriving from (execution, activity, sequence) fixes both halves at once. It is a pure function
/// of state the envelope already carries, so it replays identically; and the sequence is carried
/// across continuations, so it cannot repeat. Version-5 shaped so the value reads as what it is —
/// a name-based id — rather than as something random.
/// </para>
/// </summary>
internal static class DeterministicExecutionId
{
    public static ExecutionId For(ExecutionId parent, string activityName, int sequence)
    {
        var name = $"{parent}|{activityName}|{sequence}";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(name));

        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);

        // Version 5 (name-based, SHA-1) and the RFC 4122 variant.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new ExecutionId(new Guid(bytes));
    }
}
