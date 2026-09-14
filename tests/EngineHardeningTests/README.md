# Engine hardening tests

The runtime guarantees of `plans/scripting-language.md` §6, as tests. Each one is an attack that a
workflow author could write, and the assertion is that it ends as a failed evaluation rather than a
changed input, an escaped function, or a dead process.

**What is under test today is `Support/ReferenceEvaluator`**, the posture the language migration
must reproduce: the grammar (a JavaScript subset with no loops, classes or prototypes, checked on
the tree Jint runs), the mandatory engine options, the surface allow-list, host-side freezing of
the scope, one engine per activity, and the result converter. There is no mode that evaluates
more than the subset: the runtime-layer attacks are written *inside* the language, which means
their iteration count comes from the input they are handed — `reduce` over a 20,000-element array
builds a 20,000-deep structure — and that is the more honest attack, since the data carries the
cost. When `InProcessEvaluator` lands in
`Execution.Infrastructure`, these tests point at it and the copy here is deleted. Until then this
suite is the executable form of the plan.

**Why it is not in the CI unit-test step.** It is adversarial by design: 45,000-deep structures,
catastrophic regexes, allocation against the limit. With the options set correctly every case
completes in well under a second, but the value of the suite is that a regression in one option —
`StackOverflowGuard` above all — is a failing test, and one of those regressions would take the
test host down with it. It runs weekly and on demand (`.github/workflows/engine-hardening.yml`)
and locally with `dotnet test tests/EngineHardeningTests`.

**[sebastienros/jint#4076](https://github.com/sebastienros/jint/issues/4076)** — a property miss on
a 20,000-link prototype chain recurses in `ObjectInstance.Get`, which `StackOverflowGuard` does not
probe, and ends the process. Every way to build such a chain is outside the language, so
`GrammarTests.The_route_to_jint_4076_is_closed` pins that it is refused at parse; the plain-Jint
reproduction that was reported is in `upstream-jint-prototype-chain.md`.

The numbers the plan quotes (limits invisible to script; 121 escape attempts; `RangeError` instead
of exit `0xC00000FD`; ~2.6 ms per engine, ~5 µs per expression) come from the spike this suite was
distilled from.
