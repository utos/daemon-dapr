# Engine hardening tests

The runtime guarantees of `api/docs/template-expressions.md` § Runtime, as tests against
**the daemon's real engine** — `JintTemplateEngine` in `Execution.Infrastructure`. Each one is an
attack that a workflow author could write, and the assertion is that it ends as a failed
evaluation with its spec code (`UTOS-E1##`) rather than a changed input, an escaped function, or
a dead process.

The grammar (`UTOS-E0##`) is the shared validator's, enforced when a bundle is loaded;
`GrammarTests` drive `Utos.Workflow.Validation` on a minimal bundle so the two layers are tested
where each lives. There is no mode that evaluates more than the subset: the runtime attacks are
written *inside* the language, which means their iteration count comes from the input they are
handed — `reduce` over a 20,000-element array builds a 20,000-deep structure — and that is the
more honest attack, since the data carries the cost.

**Why it is not in the CI unit-test step.** It is adversarial by design: 45,000-deep structures,
catastrophic regexes, allocation against the limit. With the options set correctly every case
completes in well under a second, but the value of the suite is that a regression in one option —
`StackOverflowGuard` above all — is a failing test, and one of those regressions would take the
test host down with it. It runs weekly and on demand (`.github/workflows/engine-hardening.yml`)
and locally with `dotnet test tests/EngineHardeningTests`.

**[sebastienros/jint#4076](https://github.com/sebastienros/jint/issues/4076)** — a property miss on
a 20,000-link prototype chain recurses in `ObjectInstance.Get`, which `StackOverflowGuard` does not
probe, and ends the process. Every way to build such a chain is outside the language, so
`GrammarTests.The_route_to_jint_4076_is_closed` pins that it is refused at load; the plain-Jint
reproduction that was reported is in `upstream-jint-prototype-chain.md`.
