using System.Text;
using Common.Workflow;
using Execution.Domain.Templating;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Native.Symbol;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace Execution.Infrastructure.Templating.Node;

/// <summary>
/// The <c>Blob</c> and <c>File</c> surface of <c>api/docs/template-expressions.md</c> § Blob and
/// File: the constructors, their prototypes, and the members an expression reads a blob through.
/// </summary>
internal sealed class BlobObjects(
    Engine engine,
    ObjectInstance bufferPrototype,
    IBlobReader? reader,
    long materializationLimit,
    DateTimeOffset capturedInstant)
{
    public ObjectInstance BlobPrototype { get; private set; } = null!;

    public ObjectInstance FilePrototype { get; private set; } = null!;

    /// <summary>
    /// Installs <c>Blob</c> and <c>File</c> as globals. They have to be globals rather than values
    /// handed to a scope, because <c>value instanceof Blob</c> is how the spec says a blob is
    /// recognised, and <c>instanceof</c> needs a callable right-hand side.
    /// </summary>
    public static BlobObjects Install(
        Engine engine,
        ObjectInstance bufferPrototype,
        IBlobReader? reader,
        long materializationLimit,
        DateTimeOffset capturedInstant)
    {
        var objects = new BlobObjects(engine, bufferPrototype, reader, materializationLimit, capturedInstant);

        objects.BlobPrototype = objects.CreateBlobPrototype();
        objects.FilePrototype = objects.CreateFilePrototype(objects.BlobPrototype);

        // A plain script function has [[Construct]] where a ClrFunction has only [[Call]], so each
        // class is one that hands off to the host factory and returns its object — the same bridge
        // UrlObjects uses, and the reason `new Blob(...)` works at all. `File.prototype` inherits
        // from `Blob.prototype`, which is what makes `file instanceof Blob` true, as in Node.
        engine.SetValue("__Blob", new ClrFunction(engine, "Blob", (_, args) => objects.Construct(args, file: false)));
        engine.SetValue("__File", new ClrFunction(engine, "File", (_, args) => objects.Construct(args, file: true)));
        engine.SetValue("__blobPrototype", objects.BlobPrototype);
        engine.SetValue("__filePrototype", objects.FilePrototype);

        engine.Execute("""
            (function () {
                const makeBlob = __Blob, makeFile = __File;
                const blobPrototype = __blobPrototype, filePrototype = __filePrototype;
                function Blob(parts, options) { return makeBlob(parts, options); }
                function File(parts, name, options) { return makeFile(parts, name, options); }
                Blob.prototype = blobPrototype; blobPrototype.constructor = Blob;
                File.prototype = filePrototype; filePrototype.constructor = File;
                globalThis.Blob = Blob;
                globalThis.File = File;
            })();
            """);

        return objects;
    }

    public BlobInstance Create(WorkflowBlob handle) =>
        new(engine, handle.Name is null ? BlobPrototype : FilePrototype, handle);

    private ObjectInstance CreateBlobPrototype()
    {
        var prototype = new JsObject(engine);

        Method(prototype, "slice", (self, args) => Slice(Self(self), args));
        Method(prototype, "bytes", (self, args) => Settled(ReadBuffer(Self(self))));
        Method(prototype, "text", (self, args) => Settled(ReadText(Self(self))));

        // A blob has no implicit text. Node returns '[object Blob]' and '{}'; that difference is
        // the point, because before 0.20 `response.body.toString('utf8')` was how a body was read,
        // and on a Blob it would otherwise return the literal text '[object Blob]' — a wrong value
        // where an error belongs.
        Refuse(prototype, "toString");
        Refuse(prototype, "valueOf");
        Refuse(prototype, "toJSON");
        RefuseSymbol(prototype, GlobalSymbolRegistry.ToPrimitive);

        return prototype;
    }

    private ObjectInstance CreateFilePrototype(ObjectInstance blobPrototype)
    {
        var prototype = new JsObject(engine) { Prototype = blobPrototype };

        return prototype;
    }

    /// <summary>
    /// <c>new Blob(parts, options)</c> / <c>new File(parts, name, options)</c>. Parts are strings
    /// (UTF-8), Buffers and Blobs, concatenated — so constructing from a Blob reads it, and that
    /// read is bounded like any other.
    /// </summary>
    private BlobInstance Construct(JsValue[] args, bool file)
    {
        var parts = args.Length > 0 ? args[0] : JsValue.Undefined;
        var name = file ? Argument(args, 1) : JsValue.Undefined;
        var options = file ? Argument(args, 2) : Argument(args, 1);

        var bytes = Concatenate(parts);

        var type = MediaType(options);
        var lastModified = file ? LastModified(options) : (DateTimeOffset?)null;

        var handle = new WorkflowBlob(
            Data: bytes,
            Size: bytes.Length,
            MediaType: type,
            Name: file ? (name.IsUndefined() ? string.Empty : name.ToString()) : null,
            LastModified: lastModified);

        return Create(handle);
    }

    private static bool IsArray(ObjectInstance value) => value is JsArray;

    private byte[] Concatenate(JsValue parts)
    {
        if (parts.IsUndefined() || parts.IsNull()) return [];

        if (parts is not ObjectInstance array || !IsArray(array))
            throw new JavaScriptException(engine.Intrinsics.TypeError,
                "The first argument must be an array of parts.");

        var buffer = new List<byte>();

        foreach (var item in array.GetOwnPropertyKeys(Types.String)
                     .Where(key => uint.TryParse(key.AsString(), out _))
                     .OrderBy(key => uint.Parse(key.AsString()))
                     .Select(key => array.Get(key)))
        {
            switch (item)
            {
                case BufferInstance buf:
                    buffer.AddRange(buf.Bytes);
                    break;

                case BlobInstance blob:
                    buffer.AddRange(ReadBytes(blob));
                    break;

                default:
                    buffer.AddRange(Encoding.UTF8.GetBytes(item.ToString()));
                    break;
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Node's arithmetic: negative indices count from the end, both are clamped, and
    /// <c>contentType</c> defaults to <c>''</c>. <b>Reads nothing</b> — a slice of a stored blob is
    /// the same object at another offset, which is what makes slicing a large blob free.
    /// </summary>
    private JsValue Slice(BlobInstance self, JsValue[] args)
    {
        var size = self.Handle.Size;
        var start = Index(Argument(args, 0), 0, size);
        var end = Index(Argument(args, 1), size, size);
        var length = Math.Max(0, end - start);

        var type = args.Length > 2 && !args[2].IsUndefined() ? Normalise(args[2].ToString()) : string.Empty;

        // Always a Blob, never a File, even when sliced from one — as in Node.
        var handle = self.Handle.Data is { } inline
            ? new WorkflowBlob(Data: inline.Skip((int)start).Take((int)length).ToArray(), Size: length, MediaType: type)
            : new WorkflowBlob(self.Handle.Id, Offset: self.Handle.Offset + start, Size: length, MediaType: type);

        return new BlobInstance(engine, BlobPrototype, handle);
    }

    private static long Index(JsValue value, long fallback, long size)
    {
        if (value.IsUndefined()) return fallback;

        var raw = (long)value.AsNumber();
        if (raw < 0) raw += size;

        return Math.Clamp(raw, 0, size);
    }

    private JsValue ReadBuffer(BlobInstance self) =>
        new BufferInstance(engine, bufferPrototype, ReadBytes(self));

    /// <summary>UTF-8 as Node decodes it: a leading BOM removed, invalid sequences U+FFFD.</summary>
    private JsValue ReadText(BlobInstance self)
    {
        var bytes = ReadBytes(self);
        var text = new UTF8Encoding(false, false).GetString(bytes);

        return new JsString(text.Length > 0 && text[0] == '﻿' ? text[1..] : text);
    }

    /// <summary>
    /// The bytes, bounded. The limit is checked <em>before</em> anything is read, which a blob's
    /// always-known size makes possible — so a 2 GiB blob costs a refusal rather than a read.
    /// </summary>
    private byte[] ReadBytes(BlobInstance self)
    {
        var handle = self.Handle;

        if (handle.Size > materializationLimit)
            throw new TemplateException(EvaluationCodes.Memory,
                $"Reading {handle.Size} bytes exceeds the materialization limit of "
                + $"{materializationLimit}. Slice the blob first: `blob.slice(0, n)`.");

        if (handle.Data is { } inline)
            return inline;

        if (reader is null)
            throw new TemplateException(BlobUnreadableException.Code,
                "A stored blob cannot be read: this daemon has no blob store configured.");

        try
        {
            return reader.Read(handle);
        }
        catch (BlobUnreadableException unreadable)
        {
            throw new TemplateException(BlobUnreadableException.Code, unreadable.Message, unreadable);
        }
    }

    /// <summary>
    /// A promise that is already settled, which is the whole of § Awaiting's concurrency model: the
    /// read happened when the member was called, so awaiting it changes nothing about when.
    /// </summary>
    private JsValue Settled(JsValue value)
    {
        var (promise, resolve, _) = engine.Advanced.RegisterPromise();
        resolve(value);
        return promise;
    }

    private string MediaType(JsValue options)
    {
        if (options is not ObjectInstance obj) return string.Empty;

        var type = obj.Get("type");
        return type.IsUndefined() ? string.Empty : Normalise(type.ToString());
    }

    private DateTimeOffset LastModified(JsValue options)
    {
        if (options is ObjectInstance obj)
        {
            var value = obj.Get("lastModified");
            if (!value.IsUndefined())
                return DateTimeOffset.FromUnixTimeMilliseconds((long)value.AsNumber());
        }

        // The captured instant, so a constructed File is deterministic exactly as `Date.now()` is.
        return capturedInstant;
    }

    /// <summary>Lowercased, and `''` when not printable ASCII — as Node.</summary>
    private static string Normalise(string type) =>
        type.All(c => c >= 0x20 && c <= 0x7E) ? type.ToLowerInvariant() : string.Empty;

    private static JsValue Argument(JsValue[] args, int index) =>
        index < args.Length ? args[index] : JsValue.Undefined;

    private BlobInstance Self(JsValue self) =>
        self as BlobInstance
        ?? throw new JavaScriptException(engine.Intrinsics.TypeError, "not a Blob");

    private void Method(ObjectInstance target, string name, Func<JsValue, JsValue[], JsValue> body) =>
        BufferObjects.Method(engine, target, name, body);

    private void Refuse(ObjectInstance target, string name) =>
        Method(target, name, (_, _) => throw new TemplateException(
            EvaluationCodes.ScriptError,
            $"A Blob has no text form: `{name}` is not available on one. "
            + "Read it with `await blob.text()`."));

    private void RefuseSymbol(ObjectInstance target, JsValue symbol) =>
        target.FastSetProperty(symbol, new Jint.Runtime.Descriptors.PropertyDescriptor(
            new ClrFunction(engine, "[Symbol.toPrimitive]", (_, _) => throw new TemplateException(
                EvaluationCodes.ScriptError,
                "A Blob has no text form. Read it with `await blob.text()`.")),
            false, false, false));
}

