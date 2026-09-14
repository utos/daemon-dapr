# Upstream issue — Jint

Posted as https://github.com/sebastienros/jint/issues/4076. Reproduction for
`NativeRecursionTests.Deep_prototype_chain_lookup_is_an_unprobed_native_recursion`, verified with
the plain-Jint program below, no Utos code involved. Kept here as the record of what was reported.

---

**Title:** `StackOverflowGuard` does not cover the prototype-chain walk in `ObjectInstance.Get`: a deep `__proto__` chain still ends the process

**Version:** Jint 4.16.2 (NuGet, built from `730db51d`, i.e. including the #4007 backport of #3877). .NET 10.0.102, Windows 11 x64, Release.

### Summary

#3877 ("Guard native recursion and forwarding paths", backported to 4.x as #4007) extended `StackOverflowGuard` to native recursion that previously killed the process — `Array.prototype.flat`, `JSON.stringify`, proxy and bound-function chains, host re-entry. With the guard on, those now raise a catchable `RangeError`, which I can confirm on 4.16.2 for 45,000-deep `JSON.stringify` and `flat(Infinity)`.

One native recursion path is not covered: a property **miss** walks the prototype chain through `ObjectInstance.Get` → `GetOwnProperty` → prototype's `Get`, one native frame per link. A chain of ~20,000 objects overflows the native stack on a default 1 MB thread, and the guard never fires. The chain needs no API to build — `Object.setPrototypeOf` and `Object.create` can be removed from a sandboxed realm, but the `__proto__` key in an object literal sets the prototype at creation.

### Reproduction

```csharp
using Jint;

var engine = new Engine(o =>
{
    o.Constraints.StackOverflowGuard = true;
    o.MaxStatements(1_000_000);
});

engine.Evaluate("let x = {}; for (let i = 0; i < 20000; i++) x = { __proto__: x }; x.missing");
```

**Expected:** `JavaScriptException` / `RangeError: Maximum call stack size exceeded`, as for `JSON.stringify` on the same depth.

**Actual:** the process ends with a native stack overflow (Windows exit `0xC00000FD`); nothing is thrown or logged.

```
Stack overflow.
   at Jint.Extensions.Hash.GetFNVHashCode(System.String)
   at Jint.Key..ctor(System.String)
   at Jint.Key.op_Implicit(System.String)
   at Jint.Native.Object.ObjectInstance.GetOwnProperty(Jint.Native.JsValue)
   at Jint.Native.Object.ObjectInstance.Get(Jint.Native.JsValue, Jint.Native.JsValue)
   at Jint.Runtime.Interpreter.Expressions.JintMemberExpression.ReadAfterOwnMissUncached(...)
   at Jint.Runtime.Interpreter.Expressions.JintMemberExpression.ReadAfterOwnMiss(...)
   at Jint.Runtime.Interpreter.Expressions.JintMemberExpression.GetValue(...)
   (ObjectInstance.Get repeats once per prototype link)
```

### Variations tried

| Configuration | Result |
|---|---|
| `StackOverflowGuard = true` | process ends |
| `StackOverflowGuard = true` + `LimitRecursion(64)` | process ends (no JS call is involved, so the frame counter sees nothing) |
| `StackOverflowGuard = false` | process ends |
| `StackOverflowGuard = true`, evaluated on a `new Thread(…, 256 MB)` | returns `undefined` in ~530 ms |

So the outcome depends only on the calling thread's headroom, which is the situation the option's documentation describes as the one it exists to remove ("no `catch` sees it, no exception is raised, nothing is logged, and the process is simply gone").

For contrast, on the same engine and thread:

```js
let x = []; for (let i = 0; i < 45000; i++) x = [x]; JSON.stringify(x)   // RangeError, ~200 ms ✔
let x = []; for (let i = 0; i < 45000; i++) x = [x]; x.flat(Infinity)    // RangeError ✔
```

### Suggestion

The chain walk in `ObjectInstance.Get` (and presumably `HasProperty`/`Set` on a miss, which I have not tried) looks like a candidate for the same `EnsureNativeStackHeadroom` probe #3877 placed on `flat` and the JSON serializer — or, since the chain is a linked list, an iterative walk that needs no stack at all. Either would make `x.missing` on a deep chain a `RangeError` rather than a process exit, consistent with the rest of the guard.

Happy to test a fix against the reproduction above; I have it as an xunit test (currently skipped, because it takes the test host down).
