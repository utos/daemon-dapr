using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace Execution.Infrastructure.Templating;

/// <summary>
/// <c>utos.*</c> — § Host library of the spec. Spec surface, so it stays small: each function is
/// a conformance fixture.
/// </summary>
internal static class HostLibrary
{
    public static void Install(Engine engine)
    {
        var utos = new JsObject(engine);
        utos.Set("base64Decode", Function(engine, "base64Decode", s => Decode(s, urlSafe: false)));
        utos.Set("base64UrlDecode", Function(engine, "base64UrlDecode", s => Decode(s, urlSafe: true)));
        utos.Set("base64Encode", Function(engine, "base64Encode", s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))));
        engine.SetValue("utos", utos);
    }

    private static ClrFunction Function(Engine engine, string name, Func<string, string> body) =>
        new(engine, name, (_, arguments) =>
        {
            var argument = arguments.Length > 0 ? arguments[0] : JsValue.Undefined;
            if (!argument.IsString())
                throw new JavaScriptException(engine.Intrinsics.TypeError, $"utos.{name} expects a string");
            try
            {
                return new JsString(body(argument.AsString()));
            }
            catch (FormatException error)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, $"utos.{name}: {error.Message}");
            }
        });

    private static string Decode(string value, bool urlSafe)
    {
        if (urlSafe)
        {
            value = value.Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
