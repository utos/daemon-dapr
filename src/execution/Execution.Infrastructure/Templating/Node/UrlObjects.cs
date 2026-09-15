using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

namespace Execution.Infrastructure.Templating.Node;

/// <summary>
/// <c>URL</c> and <c>URLSearchParams</c> as the spec lists them: parsing, the components, and a
/// <c>searchParams</c> whose mutations write back to the URL. Parsing is <see cref="Uri"/>'s,
/// which agrees with the WHATWG parser on every URL a workflow is likely to build and differs on
/// the corners (non-special schemes, exotic escapes); serialisation of the query is the
/// <c>application/x-www-form-urlencoded</c> rules, implemented here because nothing in .NET
/// produces Node's exact form.
/// </summary>
internal static class UrlObjects
{
    public static void Install(Engine engine)
    {
        var paramsPrototype = new JsObject(engine);
        var urlPrototype = new JsObject(engine);

        // URLSearchParams ---------------------------------------------------------------------

        // Construction goes through a script-defined function below, because `new` needs
        // [[Construct]] and a ClrFunction has only [[Call]]; the host factory builds the object.
        engine.SetValue("__URLSearchParams", new ClrFunction(engine, "URLSearchParams", (_, args) =>
        {
            var instance = new SearchParamsInstance(engine, paramsPrototype, null);
            if (args.Length > 0 && !args[0].IsUndefined()) instance.Initialise(engine, args[0]);
            return instance;
        }));

        BufferObjects.Method(engine, paramsPrototype, "get", (self, args) =>
        {
            var key = Key(args);
            var found = Params(engine, self).Pairs.FirstOrDefault(p => p.Key == key);
            return found.Key == null ? JsValue.Null : new JsString(found.Value);
        });
        BufferObjects.Method(engine, paramsPrototype, "getAll", (self, args) =>
        {
            var key = Key(args);
            return new JsArray(engine, [.. Params(engine, self).Pairs.Where(p => p.Key == key).Select(p => (JsValue)new JsString(p.Value))]);
        });
        BufferObjects.Method(engine, paramsPrototype, "has", (self, args) =>
        {
            var key = Key(args);
            return Params(engine, self).Pairs.Any(p => p.Key == key) ? JsBoolean.True : JsBoolean.False;
        });
        BufferObjects.Method(engine, paramsPrototype, "set", (self, args) =>
        {
            var instance = Params(engine, self);
            var key = Key(args);
            var value = args.Length > 1 ? TypeConverter.ToString(args[1]) : "undefined";
            var index = instance.Pairs.FindIndex(p => p.Key == key);
            instance.Pairs.RemoveAll(p => p.Key == key);
            if (index < 0) instance.Pairs.Add((key, value));
            else instance.Pairs.Insert(Math.Min(index, instance.Pairs.Count), (key, value));
            instance.Changed();
            return JsValue.Undefined;
        });
        BufferObjects.Method(engine, paramsPrototype, "append", (self, args) =>
        {
            var instance = Params(engine, self);
            instance.Pairs.Add((Key(args), args.Length > 1 ? TypeConverter.ToString(args[1]) : "undefined"));
            instance.Changed();
            return JsValue.Undefined;
        });
        BufferObjects.Method(engine, paramsPrototype, "delete", (self, args) =>
        {
            var instance = Params(engine, self);
            var key = Key(args);
            instance.Pairs.RemoveAll(p => p.Key == key);
            instance.Changed();
            return JsValue.Undefined;
        });
        BufferObjects.Method(engine, paramsPrototype, "sort", (self, _) =>
        {
            var instance = Params(engine, self);
            var sorted = instance.Pairs.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            instance.Pairs.Clear();
            instance.Pairs.AddRange(sorted);
            instance.Changed();
            return JsValue.Undefined;
        });
        // Arrays rather than iterators: the language has no `for…of`, so an array is what map
        // and spread work over.
        BufferObjects.Method(engine, paramsPrototype, "keys", (self, _) =>
            new JsArray(engine, [.. Params(engine, self).Pairs.Select(p => (JsValue)new JsString(p.Key))]));
        BufferObjects.Method(engine, paramsPrototype, "values", (self, _) =>
            new JsArray(engine, [.. Params(engine, self).Pairs.Select(p => (JsValue)new JsString(p.Value))]));
        BufferObjects.Method(engine, paramsPrototype, "entries", (self, _) =>
            new JsArray(engine, [.. Params(engine, self).Pairs.Select(p => (JsValue)new JsArray(engine, [new JsString(p.Key), new JsString(p.Value)]))]));
        BufferObjects.Method(engine, paramsPrototype, "forEach", (self, args) =>
        {
            var callback = args.Length > 0 ? args[0] : JsValue.Undefined;
            foreach (var (key, value) in Params(engine, self).Pairs.ToList())
                engine.Invoke(callback, new JsString(value), new JsString(key), self);
            return JsValue.Undefined;
        });
        BufferObjects.Method(engine, paramsPrototype, "toString", (self, _) => new JsString(Params(engine, self).Serialise()));

        // URL ---------------------------------------------------------------------------------

        engine.SetValue("__URL", new ClrFunction(engine, "URL", (_, args) =>
        {
            var input = args.Length > 0 ? TypeConverter.ToString(args[0]) : "undefined";
            var hasBase = args.Length > 1 && !args[1].IsUndefined();
            Uri uri;
            try
            {
                uri = hasBase
                    ? new Uri(new Uri(TypeConverter.ToString(args[1]), UriKind.Absolute), input)
                    : new Uri(input, UriKind.Absolute);
            }
            catch (UriFormatException)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "Invalid URL");
            }

            return new UrlInstance(engine, urlPrototype, paramsPrototype, uri);
        }));

        BufferObjects.Method(engine, urlPrototype, "toString", (self, _) => new JsString(Url(engine, self).Href));
        BufferObjects.Method(engine, urlPrototype, "toJSON", (self, _) => new JsString(Url(engine, self).Href));

        // A plain function has [[Construct]], so each class is one that hands off to the host
        // factory and returns its object — which `new` then yields as-is. The `__` names are
        // removed by the lockdown sweep; the closures keep the factories.
        engine.SetValue("__urlPrototype", urlPrototype);
        engine.SetValue("__paramsPrototype", paramsPrototype);
        engine.Execute("""
            (function () {
                const makeUrl = __URL, makeParams = __URLSearchParams;
                const urlPrototype = __urlPrototype, paramsPrototype = __paramsPrototype;
                function URL(input, base) { return makeUrl(input, base); }
                function URLSearchParams(init) { return makeParams(init); }
                URL.prototype = urlPrototype; urlPrototype.constructor = URL;
                URLSearchParams.prototype = paramsPrototype; paramsPrototype.constructor = URLSearchParams;
                globalThis.URL = URL;
                globalThis.URLSearchParams = URLSearchParams;
            })();
            """);
    }

    private static string Key(JsValue[] args) => args.Length > 0 ? TypeConverter.ToString(args[0]) : "undefined";

    private static SearchParamsInstance Params(Engine engine, JsValue self) =>
        self as SearchParamsInstance
        ?? throw new JavaScriptException(engine.Intrinsics.TypeError, "URLSearchParams method called on something that is not a URLSearchParams");

    private static UrlInstance Url(Engine engine, JsValue self) =>
        self as UrlInstance
        ?? throw new JavaScriptException(engine.Intrinsics.TypeError, "URL method called on something that is not a URL");

    // ---- application/x-www-form-urlencoded ------------------------------------------------------

    private static string FormEncode(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b == ' ') builder.Append('+');
            else if (b is (>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z')
                     || b is (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_')
                builder.Append((char)b);
            else builder.Append('%').Append(b.ToString("X2"));
        }

        return builder.ToString();
    }

    private static string FormDecode(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    private static List<(string Key, string Value)> ParseQuery(string query)
    {
        var pairs = new List<(string, string)>();
        if (query.StartsWith('?')) query = query[1..];
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            pairs.Add(eq < 0
                ? (FormDecode(part), string.Empty)
                : (FormDecode(part[..eq]), FormDecode(part[(eq + 1)..])));
        }

        return pairs;
    }

    // ---- instances --------------------------------------------------------------------------------

    internal sealed class SearchParamsInstance : ObjectInstance
    {
        private readonly UrlInstance? _owner;

        public SearchParamsInstance(Engine engine, ObjectInstance prototype, UrlInstance? owner) : base(engine)
        {
            _owner = owner;
            Prototype = prototype;
        }

        public List<(string Key, string Value)> Pairs { get; } = [];

        public void Initialise(Engine engine, JsValue init)
        {
            if (init.IsString())
            {
                Pairs.AddRange(ParseQuery(init.AsString()));
            }
            else if (init is SearchParamsInstance other)
            {
                Pairs.AddRange(other.Pairs);
            }
            else if (init.IsArray())
            {
                var array = init.AsArray();
                for (uint i = 0; i < array.Length; i++)
                {
                    var pair = array.Get(i);
                    if (!pair.IsArray() || pair.AsArray().Length != 2)
                        throw new JavaScriptException(engine.Intrinsics.TypeError, "URLSearchParams: each entry must be a [name, value] pair");
                    Pairs.Add((TypeConverter.ToString(pair.AsArray().Get(0)), TypeConverter.ToString(pair.AsArray().Get(1))));
                }
            }
            else if (init.IsObject())
            {
                var obj = init.AsObject();
                foreach (var key in obj.GetOwnPropertyKeys(Types.String))
                {
                    var descriptor = obj.GetOwnProperty(key);
                    if (!descriptor.Enumerable) continue;
                    Pairs.Add((key.AsString(), TypeConverter.ToString(obj.Get(key))));
                }
            }
            else
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "URLSearchParams: the argument must be a string, an object or an array of pairs");
            }
        }

        public string Serialise() =>
            string.Join("&", Pairs.Select(p => FormEncode(p.Key) + "=" + FormEncode(p.Value)));

        public void Changed() => _owner?.QueryChanged(Serialise());

        public override PropertyDescriptor GetOwnProperty(JsValue property)
        {
            if (property.IsString() && property.AsString() == "size")
                return new PropertyDescriptor(JsNumber.Create(Pairs.Count), writable: false, enumerable: false, configurable: false);
            return base.GetOwnProperty(property);
        }
    }

    internal sealed class UrlInstance : ObjectInstance
    {
        private static readonly HashSet<string> Components =
        [
            "href", "protocol", "origin", "host", "hostname", "port", "pathname", "search", "hash",
            "username", "password", "searchParams",
        ];

        private readonly SearchParamsInstance _searchParams;
        private Uri _uri;

        public UrlInstance(Engine engine, ObjectInstance prototype, ObjectInstance paramsPrototype, Uri uri) : base(engine)
        {
            _uri = uri;
            _searchParams = new SearchParamsInstance(engine, paramsPrototype, this);
            _searchParams.Pairs.AddRange(ParseQuery(uri.Query));
            Prototype = prototype;
        }

        public string Href => _uri.AbsoluteUri;

        public void QueryChanged(string serialised)
        {
            var builder = new UriBuilder(_uri) { Query = serialised };
            _uri = builder.Uri;
        }

        public override PropertyDescriptor GetOwnProperty(JsValue property)
        {
            if (property.IsString() && Components.Contains(property.AsString()))
                return new PropertyDescriptor(Component(property.AsString()), writable: true, enumerable: true, configurable: false);
            return base.GetOwnProperty(property);
        }

        public override bool Set(JsValue property, JsValue value, JsValue receiver)
        {
            if (!property.IsString() || !Components.Contains(property.AsString()))
                return base.Set(property, value, receiver);

            var text = TypeConverter.ToString(value);
            var builder = new UriBuilder(_uri);
            switch (property.AsString())
            {
                case "pathname": builder.Path = text; break;
                case "search": builder.Query = text; break;
                case "hash": builder.Fragment = text; break;
                case "href":
                    if (!Uri.TryCreate(text, UriKind.Absolute, out var replaced))
                        throw new JavaScriptException(_engine.Intrinsics.TypeError, "Invalid URL");
                    builder = new UriBuilder(replaced);
                    break;
                default:
                    // The remaining components are read-only in this version; strict mode reports
                    // the refused write as a TypeError.
                    return false;
            }

            _uri = builder.Uri;
            _searchParams.Pairs.Clear();
            _searchParams.Pairs.AddRange(ParseQuery(_uri.Query));
            return true;
        }

        public override List<JsValue> GetOwnPropertyKeys(Types types = Types.Empty | Types.String | Types.Symbol)
        {
            var keys = new List<JsValue>();
            if ((types & Types.String) != 0)
                keys.AddRange(Components.Select(name => (JsValue)new JsString(name)));
            keys.AddRange(base.GetOwnPropertyKeys(types));
            return keys;
        }

        private JsValue Component(string name)
        {
            var isDefaultPort = _uri.IsDefaultPort;
            var port = isDefaultPort ? string.Empty : _uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var hostname = _uri.Host;
            var host = isDefaultPort ? hostname : hostname + ":" + port;
            var userInfo = _uri.UserInfo.Split(':', 2);

            return name switch
            {
                "href" => new JsString(Href),
                "protocol" => new JsString(_uri.Scheme + ":"),
                "origin" => new JsString(_uri.Scheme is "http" or "https" or "ws" or "wss" ? $"{_uri.Scheme}://{host}" : "null"),
                "host" => new JsString(host),
                "hostname" => new JsString(hostname),
                "port" => new JsString(port),
                "pathname" => new JsString(_uri.AbsolutePath),
                "search" => new JsString(_uri.Query == "?" ? string.Empty : _uri.Query),
                "hash" => new JsString(_uri.Fragment == "#" ? string.Empty : _uri.Fragment),
                "username" => new JsString(userInfo[0]),
                "password" => new JsString(userInfo.Length > 1 ? userInfo[1] : string.Empty),
                "searchParams" => _searchParams,
                _ => JsValue.Undefined
            };
        }
    }
}
