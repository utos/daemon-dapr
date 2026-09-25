namespace Common.Workflow;

/// <summary>
/// Finds the blobs in a run value, with where each one sits.
/// <para>
/// Two callers need exactly this, and for opposite reasons. Promotion walks a root execution's
/// output to learn which blobs somebody asked for; <c>ScheduleExecution</c> walks a client's input to
/// check each one, and reports a failure per blob as a <c>google.rpc.BadRequest</c> field violation
/// whose <c>field</c> is a JSON Pointer. One walk rather than two, because a walk that discards
/// position and a walk that keeps it are the same walk with one argument's difference — and two of
/// them would eventually disagree about what counts as a blob.
/// </para>
/// </summary>
public static class BlobWalk
{
    /// <summary>
    /// Every blob in <paramref name="value"/>, each with its JSON Pointer (RFC 6901) from the root.
    /// Depth-first in document order, so a report reads in the order a person wrote the document.
    /// </summary>
    /// <param name="root">
    /// What the pointers are relative to. <c>""</c> for a whole value; a caller walking one field of
    /// a request passes that field's pointer.
    /// </param>
    public static IEnumerable<(string Pointer, WorkflowBlob Blob)> Locate(
        object? value, string root = "")
    {
        switch (value)
        {
            case WorkflowBlob blob:
                yield return (root, blob);
                break;

            case IDictionary<string, object?> map:
                foreach (var (key, item) in map)
                foreach (var found in Locate(item, root + "/" + EscapePointer(key)))
                    yield return found;
                break;

            // Not `IEnumerable`: a string is one, and so is a dictionary, and walking either as a
            // sequence would be wrong in a way that only shows up on the values that have blobs in
            // them. Run values are the shapes the value converter produces, and a list is this one.
            case System.Collections.IList list:
                for (var index = 0; index < list.Count; index++)
                foreach (var found in Locate(list[index], root + "/" + index))
                    yield return found;
                break;
        }
    }

    /// <summary>Just the blobs, for a caller that does not care where they were.</summary>
    public static IReadOnlyList<WorkflowBlob> In(object? value) =>
        Locate(value).Select(found => found.Blob).ToList();

    /// <summary>
    /// A JSON Pointer reference token: <c>~</c> becomes <c>~0</c> and <c>/</c> becomes <c>~1</c>, in
    /// that order. Required rather than cosmetic — a key containing a slash would otherwise read as
    /// two levels of nesting, and point at nothing.
    /// </summary>
    private static string EscapePointer(string key) =>
        key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
