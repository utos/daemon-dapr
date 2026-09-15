using System.Buffers.Binary;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace Execution.Infrastructure.Templating.Node;

/// <summary>
/// The <c>Buffer</c> surface of the spec: the constructor object's statics and the prototype's
/// members, on <see cref="BufferInstance"/>. Encodings are Node's names and Node's leniency —
/// <c>base64</c> accepts the URL-safe alphabet, whitespace and missing padding, <c>hex</c> stops
/// at the first bad pair — so that data copied from a Node program decodes the same way here.
/// </summary>
internal static class BufferObjects
{
    public static ObjectInstance CreatePrototype(Engine engine)
    {
        var prototype = new JsObject(engine);

        Method(engine, prototype, "toString", (self, args) =>
        {
            var bytes = Self(engine, self).Bytes;
            var encoding = OptionalEncoding(engine, args, 0);
            var start = Clamp(OptionalInt(args, 1, 0), bytes.Length);
            var end = Clamp(OptionalInt(args, 2, bytes.Length), bytes.Length);
            return new JsString(end <= start ? string.Empty : Encode(bytes.AsSpan(start, end - start), encoding));
        });

        Method(engine, prototype, "slice", (self, args) => Slice(engine, self, args));
        Method(engine, prototype, "subarray", (self, args) => Slice(engine, self, args));

        Method(engine, prototype, "equals", (self, args) =>
            Self(engine, self).Bytes.AsSpan().SequenceEqual(Other(engine, args, 0, "equals").Bytes) ? JsBoolean.True : JsBoolean.False);

        Method(engine, prototype, "compare", (self, args) =>
            JsNumber.Create(Math.Sign(Self(engine, self).Bytes.AsSpan().SequenceCompareTo(Other(engine, args, 0, "compare").Bytes))));

        Method(engine, prototype, "indexOf", (self, args) => JsNumber.Create(IndexOf(engine, self, args)));
        Method(engine, prototype, "includes", (self, args) => IndexOf(engine, self, args) >= 0 ? JsBoolean.True : JsBoolean.False);

        Method(engine, prototype, "at", (self, args) =>
        {
            var bytes = Self(engine, self).Bytes;
            var index = OptionalInt(args, 0, 0);
            if (index < 0) index += bytes.Length;
            return index < 0 || index >= bytes.Length ? JsValue.Undefined : JsNumber.Create(bytes[index]);
        });

        Read(engine, prototype, "readUInt8", 1, span => span[0]);
        Read(engine, prototype, "readInt8", 1, span => (sbyte)span[0]);
        Read(engine, prototype, "readUInt16LE", 2, span => BinaryPrimitives.ReadUInt16LittleEndian(span));
        Read(engine, prototype, "readUInt16BE", 2, span => BinaryPrimitives.ReadUInt16BigEndian(span));
        Read(engine, prototype, "readInt16LE", 2, span => BinaryPrimitives.ReadInt16LittleEndian(span));
        Read(engine, prototype, "readInt16BE", 2, span => BinaryPrimitives.ReadInt16BigEndian(span));
        Read(engine, prototype, "readUInt32LE", 4, span => BinaryPrimitives.ReadUInt32LittleEndian(span));
        Read(engine, prototype, "readUInt32BE", 4, span => BinaryPrimitives.ReadUInt32BigEndian(span));
        Read(engine, prototype, "readInt32LE", 4, span => BinaryPrimitives.ReadInt32LittleEndian(span));
        Read(engine, prototype, "readInt32BE", 4, span => BinaryPrimitives.ReadInt32BigEndian(span));

        // Node's JSON form. Deliberately not what leaves an expression — a Buffer as a whole result
        // is refused (UTOS-E103) — but JSON.stringify inside one calls this, as Node's does.
        Method(engine, prototype, "toJSON", (self, _) =>
        {
            var bytes = Self(engine, self).Bytes;
            var data = new JsArray(engine, [.. bytes.Select(b => (JsValue)JsNumber.Create(b))]);
            var result = new JsObject(engine);
            result.Set("type", new JsString("Buffer"));
            result.Set("data", data);
            return result;
        });

        return prototype;
    }

    public static ObjectInstance CreateConstructor(Engine engine, ObjectInstance prototype)
    {
        // An object rather than a function: `new Buffer()` and `Buffer()` are deprecated in Node
        // and absent from the spec's surface, so there is nothing to call.
        var buffer = new JsObject(engine);
        buffer.Set("prototype", prototype);
        prototype.Set("constructor", buffer);

        Method(engine, buffer, "from", (_, args) =>
        {
            var source = args.Length > 0 ? args[0] : JsValue.Undefined;
            if (source.IsString())
                return new BufferInstance(engine, prototype, Decode(engine, source.AsString(), OptionalEncoding(engine, args, 1)));
            if (source is BufferInstance other)
                return new BufferInstance(engine, prototype, (byte[])other.Bytes.Clone());
            if (source.IsArray())
            {
                var array = source.AsArray();
                var bytes = new byte[array.Length];
                for (var i = 0; i < bytes.Length; i++)
                    bytes[i] = (byte)((long)TypeConverter.ToNumber(array.Get(i)) & 0xFF);
                return new BufferInstance(engine, prototype, bytes);
            }

            throw new JavaScriptException(engine.Intrinsics.TypeError,
                "Buffer.from: the first argument must be a string, a Buffer or an array of numbers");
        });

        Method(engine, buffer, "isBuffer", (_, args) =>
            args.Length > 0 && args[0] is BufferInstance ? JsBoolean.True : JsBoolean.False);

        Method(engine, buffer, "byteLength", (_, args) =>
        {
            var source = args.Length > 0 ? args[0] : JsValue.Undefined;
            if (source is BufferInstance other) return JsNumber.Create(other.Bytes.Length);
            if (source.IsString()) return JsNumber.Create(Decode(engine, source.AsString(), OptionalEncoding(engine, args, 1)).Length);
            throw new JavaScriptException(engine.Intrinsics.TypeError, "Buffer.byteLength: the argument must be a string or a Buffer");
        });

        Method(engine, buffer, "concat", (_, args) =>
        {
            if (args.Length == 0 || !args[0].IsArray())
                throw new JavaScriptException(engine.Intrinsics.TypeError, "Buffer.concat: the argument must be an array of Buffers");
            var list = args[0].AsArray();
            var parts = new List<byte[]>();
            for (uint i = 0; i < list.Length; i++)
            {
                if (list.Get(i) is not BufferInstance part)
                    throw new JavaScriptException(engine.Intrinsics.TypeError, "Buffer.concat: every element must be a Buffer");
                parts.Add(part.Bytes);
            }

            var bytes = new byte[parts.Sum(p => p.Length)];
            var offset = 0;
            foreach (var part in parts)
            {
                part.CopyTo(bytes, offset);
                offset += part.Length;
            }

            return new BufferInstance(engine, prototype, bytes);
        });

        return buffer;
    }

    // ---- encodings ------------------------------------------------------------------------------

    public static string Encode(ReadOnlySpan<byte> bytes, string encoding) => encoding switch
    {
        "utf8" or "utf-8" => Encoding.UTF8.GetString(bytes),
        "base64" => Convert.ToBase64String(bytes),
        "base64url" => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        "hex" => Convert.ToHexString(bytes).ToLowerInvariant(),
        "latin1" or "binary" => Encoding.Latin1.GetString(bytes),
        "ascii" => Encoding.Latin1.GetString(Ascii(bytes)),
        "utf16le" or "ucs2" or "ucs-2" or "utf-16le" => Encoding.Unicode.GetString(bytes),
        _ => throw new ArgumentException(encoding)
    };

    public static byte[] Decode(Engine engine, string text, string encoding)
    {
        try
        {
            return encoding switch
            {
                "utf8" or "utf-8" => Encoding.UTF8.GetBytes(text),
                "base64" or "base64url" => Base64(text),
                "hex" => Hex(text),
                "latin1" or "binary" or "ascii" => Encoding.Latin1.GetBytes(text),
                "utf16le" or "ucs2" or "ucs-2" or "utf-16le" => Encoding.Unicode.GetBytes(text),
                _ => throw new ArgumentException(encoding)
            };
        }
        catch (FormatException error)
        {
            throw new JavaScriptException(engine.Intrinsics.TypeError, $"Buffer: not valid {encoding}: {error.Message}");
        }
    }

    /// <summary>Node's lenient base64: either alphabet, whitespace ignored, padding optional.</summary>
    private static byte[] Base64(string text)
    {
        var builder = new StringBuilder(text.Length + 3);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c == '=') continue;
            builder.Append(c switch { '-' => '+', '_' => '/', _ => c });
        }

        while (builder.Length % 4 != 0) builder.Append('=');
        return Convert.FromBase64String(builder.ToString());
    }

    /// <summary>Node's hex: decoding stops at the first character pair that is not hex.</summary>
    private static byte[] Hex(string text)
    {
        var length = text.Length / 2;
        var bytes = new List<byte>(length);
        for (var i = 0; i + 1 < text.Length; i += 2)
        {
            if (!IsHex(text[i]) || !IsHex(text[i + 1])) break;
            bytes.Add(Convert.ToByte(text.Substring(i, 2), 16));
        }

        return bytes.ToArray();
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static byte[] Ascii(ReadOnlySpan<byte> bytes)
    {
        var result = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) result[i] = (byte)(bytes[i] & 0x7F);
        return result;
    }

    private static readonly HashSet<string> Encodings =
    [
        "utf8", "utf-8", "base64", "base64url", "hex", "latin1", "binary", "ascii", "utf16le", "ucs2", "ucs-2", "utf-16le"
    ];

    public static string OptionalEncoding(Engine engine, JsValue[] args, int index)
    {
        if (args.Length <= index || args[index].IsUndefined()) return "utf8";
        var encoding = TypeConverter.ToString(args[index]).ToLowerInvariant();
        if (!Encodings.Contains(encoding))
            throw new JavaScriptException(engine.Intrinsics.TypeError, $"Unknown encoding: {encoding}");
        return encoding;
    }

    // ---- members ---------------------------------------------------------------------------------

    private static JsValue Slice(Engine engine, JsValue self, JsValue[] args)
    {
        var instance = Self(engine, self);
        var bytes = instance.Bytes;
        var start = Relative(OptionalInt(args, 0, 0), bytes.Length);
        var end = Relative(OptionalInt(args, 1, bytes.Length), bytes.Length);
        var length = Math.Max(0, end - start);
        return new BufferInstance(engine, (ObjectInstance)instance.Prototype!, bytes.AsSpan(start, length).ToArray());
    }

    private static int IndexOf(Engine engine, JsValue self, JsValue[] args)
    {
        var bytes = Self(engine, self).Bytes;
        var needle = args.Length > 0 ? args[0] : JsValue.Undefined;
        var offset = Relative(OptionalInt(args, 1, 0), bytes.Length);

        byte[] pattern;
        if (needle is BufferInstance other) pattern = other.Bytes;
        else if (needle.IsString()) pattern = Decode(engine, needle.AsString(), OptionalEncoding(engine, args, 2));
        else if (needle.IsNumber()) pattern = [(byte)((long)needle.AsNumber() & 0xFF)];
        else throw new JavaScriptException(engine.Intrinsics.TypeError, "Buffer.indexOf: the value must be a string, a Buffer or a number");

        if (pattern.Length == 0) return Math.Min(offset, bytes.Length);
        var found = bytes.AsSpan(offset).IndexOf(pattern);
        return found < 0 ? -1 : found + offset;
    }

    private static void Read<T>(Engine engine, ObjectInstance prototype, string name, int width, Func<byte[], T> read)
        where T : struct
    {
        Method(engine, prototype, name, (self, args) =>
        {
            var bytes = Self(engine, self).Bytes;
            var offset = OptionalInt(args, 0, 0);
            if (offset < 0 || offset + width > bytes.Length)
                throw new JavaScriptException(engine.Intrinsics.Error,
                    $"RangeError: {name}: offset {offset} is out of range for a Buffer of length {bytes.Length}");
            return JsNumber.Create(Convert.ToDouble(read(bytes.AsSpan(offset, width).ToArray())));
        });
    }

    private static BufferInstance Self(Engine engine, JsValue self) =>
        self as BufferInstance
        ?? throw new JavaScriptException(engine.Intrinsics.TypeError, "Buffer method called on something that is not a Buffer");

    private static BufferInstance Other(Engine engine, JsValue[] args, int index, string method) =>
        args.Length > index && args[index] is BufferInstance other
            ? other
            : throw new JavaScriptException(engine.Intrinsics.TypeError, $"Buffer.{method}: the argument must be a Buffer");

    private static int OptionalInt(JsValue[] args, int index, int fallback) =>
        args.Length > index && !args[index].IsUndefined()
            ? (int)Math.Clamp(Math.Truncate(TypeConverter.ToNumber(args[index])), int.MinValue, int.MaxValue)
            : fallback;

    private static int Clamp(int value, int length) => Math.Clamp(value, 0, length);

    /// <summary>A negative index counts from the end, as Node's slice does.</summary>
    private static int Relative(int value, int length) => Clamp(value < 0 ? length + value : value, length);

    internal static void Method(Engine engine, ObjectInstance target, string name, Func<JsValue, JsValue[], JsValue> body) =>
        target.Set(name, new ClrFunction(engine, name, body));
}
