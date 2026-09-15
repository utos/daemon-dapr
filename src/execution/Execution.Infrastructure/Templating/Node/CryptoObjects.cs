using System.Security.Cryptography;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace Execution.Infrastructure.Templating.Node;

/// <summary>
/// The <c>crypto</c> global of the spec: <c>node:crypto</c>'s synchronous hashing placed on the
/// global, since there are no modules. <c>createHash</c>, <c>createHmac</c>, <c>hash</c>,
/// <c>timingSafeEqual</c>, and <c>randomUUID</c> as the seed-derived sequence.
/// </summary>
internal static class CryptoObjects
{
    private static readonly Dictionary<string, HashAlgorithmName> Algorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sha256"] = HashAlgorithmName.SHA256,
        ["sha512"] = HashAlgorithmName.SHA512,
        ["sha384"] = HashAlgorithmName.SHA384,
        ["sha1"] = HashAlgorithmName.SHA1,
        ["md5"] = HashAlgorithmName.MD5,
    };

    public static ObjectInstance Create(Engine engine, ObjectInstance bufferPrototype, Func<string> nextUuid)
    {
        var crypto = new JsObject(engine);
        var hashPrototype = CreateHashPrototype(engine, bufferPrototype);

        BufferObjects.Method(engine, crypto, "createHash", (_, args) =>
            new HashInstance(engine, hashPrototype, IncrementalHash.CreateHash(Algorithm(engine, args, 0))));

        BufferObjects.Method(engine, crypto, "createHmac", (_, args) =>
            new HashInstance(engine, hashPrototype, IncrementalHash.CreateHMAC(Algorithm(engine, args, 0), Bytes(engine, args, 1, "createHmac", "key"))));

        // Node's one-shot form: hex by default, unlike digest(), which defaults to a Buffer.
        BufferObjects.Method(engine, crypto, "hash", (_, args) =>
        {
            using var hash = IncrementalHash.CreateHash(Algorithm(engine, args, 0));
            hash.AppendData(Bytes(engine, args, 1, "hash", "data"));
            var digest = hash.GetHashAndReset();
            var encoding = args.Length > 2 && !args[2].IsUndefined() ? BufferObjects.OptionalEncoding(engine, args, 2) : "hex";
            return new JsString(BufferObjects.Encode(digest, encoding));
        });

        BufferObjects.Method(engine, crypto, "randomUUID", (_, _) => new JsString(nextUuid()));

        BufferObjects.Method(engine, crypto, "timingSafeEqual", (_, args) =>
        {
            var a = Bytes(engine, args, 0, "timingSafeEqual", "a");
            var b = Bytes(engine, args, 1, "timingSafeEqual", "b");
            if (a.Length != b.Length)
                throw new JavaScriptException(engine.Intrinsics.Error, "RangeError: timingSafeEqual: the arguments must have the same length");
            return CryptographicOperations.FixedTimeEquals(a, b) ? JsBoolean.True : JsBoolean.False;
        });

        return crypto;
    }

    private static ObjectInstance CreateHashPrototype(Engine engine, ObjectInstance bufferPrototype)
    {
        var prototype = new JsObject(engine);

        BufferObjects.Method(engine, prototype, "update", (self, args) =>
        {
            Self(engine, self).Hash.AppendData(Bytes(engine, args, 0, "update", "data"));
            return self;
        });

        BufferObjects.Method(engine, prototype, "digest", (self, args) =>
        {
            var instance = Self(engine, self);
            if (instance.Digested)
                throw new JavaScriptException(engine.Intrinsics.Error, "Digest already called");
            instance.Digested = true;
            var digest = instance.Hash.GetHashAndReset();
            instance.Hash.Dispose();

            if (args.Length == 0 || args[0].IsUndefined())
                return new BufferInstance(engine, bufferPrototype, digest);
            return new JsString(BufferObjects.Encode(digest, BufferObjects.OptionalEncoding(engine, args, 0)));
        });

        return prototype;
    }

    private static HashAlgorithmName Algorithm(Engine engine, JsValue[] args, int index)
    {
        var name = args.Length > index && args[index].IsString() ? args[index].AsString() : string.Empty;
        return Algorithms.TryGetValue(name, out var algorithm)
            ? algorithm
            : throw new JavaScriptException(engine.Intrinsics.Error, $"Digest method not supported: {name}");
    }

    /// <summary>A string is UTF-8, as Node reads it; a Buffer is its bytes.</summary>
    private static byte[] Bytes(Engine engine, JsValue[] args, int index, string method, string parameter)
    {
        var value = args.Length > index ? args[index] : JsValue.Undefined;
        if (value is BufferInstance buffer) return buffer.Bytes;
        if (value.IsString()) return Encoding.UTF8.GetBytes(value.AsString());
        throw new JavaScriptException(engine.Intrinsics.TypeError, $"crypto.{method}: {parameter} must be a string or a Buffer");
    }

    private static HashInstance Self(Engine engine, JsValue self) =>
        self as HashInstance
        ?? throw new JavaScriptException(engine.Intrinsics.TypeError, "Hash method called on something that is not a Hash");

    private sealed class HashInstance : ObjectInstance
    {
        public HashInstance(Engine engine, ObjectInstance prototype, IncrementalHash hash) : base(engine)
        {
            Hash = hash;
            Prototype = prototype;
        }

        public IncrementalHash Hash { get; }

        public bool Digested { get; set; }
    }
}
