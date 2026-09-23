using Common.Workflow;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;

namespace Execution.Infrastructure.Templating.Node;

/// <summary>
/// A <c>Blob</c> as an expression sees it: an immutable sequence of bytes with a media type, whose
/// bytes are read on demand rather than held.
/// <para>
/// The value behind it is a <see cref="WorkflowBlob"/>, inline or stored, and the two are
/// deliberately indistinguishable here — <c>size</c>, <c>type</c> and every method behave the same
/// way, and no property reveals the backing. An author cannot write a document that works with one
/// and fails with the other (<c>api/docs/binary-data.md</c> § A blob as a value).
/// </para>
/// <para>
/// A <c>File</c> is the same instance with a name. Its prototype chain is
/// <c>File.prototype → Blob.prototype</c>, which is what makes <c>file instanceof Blob</c> true, as
/// in Node.
/// </para>
/// </summary>
internal sealed class BlobInstance : ObjectInstance
{
    public BlobInstance(Engine engine, ObjectInstance prototype, WorkflowBlob handle) : base(engine)
    {
        Handle = handle;
        Prototype = prototype;

        // Nothing else may be hung on it: a blob is immutable, and there is no surface through
        // which it could change.
        PreventExtensions();
    }

    public WorkflowBlob Handle { get; }

    public bool IsFile => Handle.Name is not null;

    /// <summary>
    /// <c>size</c> and <c>type</c>, and a File's <c>name</c> and <c>lastModified</c>. Data
    /// properties on the instance rather than accessors on the prototype, matching
    /// <c>UrlInstance</c> — and read from the handle each time, so nothing is copied for a blob
    /// whose members are never touched.
    /// </summary>
    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (property.IsString())
        {
            switch (property.AsString())
            {
                case "size":
                    return Value(JsNumber.Create(Handle.Size));

                case "type":
                    return Value(new JsString(Handle.MediaType));

                case "name" when IsFile:
                    return Value(new JsString(Handle.Name!));

                // 0 on a file whose value carried none — deterministic, where "now" would not be.
                case "lastModified" when IsFile:
                    return Value(JsNumber.Create(Handle.LastModified?.ToUnixTimeMilliseconds() ?? 0));
            }
        }

        return base.GetOwnProperty(property);
    }

    public override List<JsValue> GetOwnPropertyKeys(Types types = Types.Empty | Types.String | Types.Symbol)
    {
        var keys = new List<JsValue>();

        if ((types & Types.String) != 0)
        {
            keys.Add(new JsString("size"));
            keys.Add(new JsString("type"));

            if (IsFile)
            {
                keys.Add(new JsString("name"));
                keys.Add(new JsString("lastModified"));
            }
        }

        keys.AddRange(base.GetOwnPropertyKeys(types));
        return keys;
    }

    private static PropertyDescriptor Value(JsValue value) =>
        new(value, writable: false, enumerable: true, configurable: false);
}
