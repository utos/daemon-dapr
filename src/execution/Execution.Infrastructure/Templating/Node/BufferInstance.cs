using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;

namespace Execution.Infrastructure.Templating.Node;

/// <summary>
/// A <c>Buffer</c> as an expression sees it: a host object holding bytes, read through the
/// members <see cref="BufferObjects"/> puts on its prototype and by index. Read-only — the bytes
/// came from a response or from an encoding call, and a mutable view of either would be a route
/// to mutating scope, which nothing else in the language has.
/// <para>
/// Not a <c>Uint8Array</c> subclass and not backed by a JavaScript array: a megabyte of response
/// body is one <c>byte[]</c> here, where a JavaScript array of numbers would be a million elements
/// against the array-size limit and the memory sampler.
/// </para>
/// </summary>
internal sealed class BufferInstance : ObjectInstance
{
    public BufferInstance(Engine engine, ObjectInstance prototype, byte[] bytes) : base(engine)
    {
        Bytes = bytes;
        Prototype = prototype;
        // Bytes and length are the whole object: no expando properties either, so a Buffer built
        // by one expression of an activity carries nothing to the next but its bytes.
        PreventExtensions();
    }

    public byte[] Bytes { get; }

    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (property.IsString())
        {
            var key = property.AsString();
            if (key == "length")
                return new PropertyDescriptor(JsNumber.Create(Bytes.Length), writable: false, enumerable: false, configurable: false);

            if (TryIndex(key, out var index))
                return index < Bytes.Length
                    ? new PropertyDescriptor(JsNumber.Create(Bytes[index]), writable: false, enumerable: true, configurable: false)
                    : PropertyDescriptor.Undefined;
        }

        return base.GetOwnProperty(property);
    }

    public override bool Set(JsValue property, JsValue value, JsValue receiver)
    {
        // Strict mode turns a false return into a TypeError, which is the answer to `buf[0] = 1`.
        if (property.IsString() && (property.AsString() == "length" || TryIndex(property.AsString(), out _)))
            return false;

        return base.Set(property, value, receiver);
    }

    public override bool DefineOwnProperty(JsValue property, PropertyDescriptor desc)
    {
        if (property.IsString() && (property.AsString() == "length" || TryIndex(property.AsString(), out _)))
            return false;

        return base.DefineOwnProperty(property, desc);
    }

    public override List<JsValue> GetOwnPropertyKeys(Types types = Types.Empty | Types.String | Types.Symbol)
    {
        var keys = new List<JsValue>();
        if ((types & Types.String) != 0)
        {
            for (var i = 0; i < Bytes.Length; i++)
                keys.Add(JsString.Create(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            keys.Add(new JsString("length"));
        }

        keys.AddRange(base.GetOwnPropertyKeys(types));
        return keys;
    }

    private static bool TryIndex(string key, out int index)
    {
        index = 0;
        if (key.Length == 0 || key.Length > 9) return false;
        if (key.Length > 1 && key[0] == '0') return false;
        foreach (var c in key)
        {
            if (c < '0' || c > '9') return false;
            index = index * 10 + (c - '0');
        }

        return true;
    }
}
