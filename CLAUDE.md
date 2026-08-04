# CLAUDE.md

Guidance for working in the Utos daemon repository. The Utos spec itself, the protobuf API and
the git/commit conventions live in the parent `utos/CLAUDE.md`.

## Layering

Every module is four projects, and the names mean the same thing in all of them:

```
API.Main ──► {Module}.Api ──► {Module}.Infrastructure ──► {Module}.Application ──► {Module}.Domain
                  └──────────────────────────────────────►
```

| Layer | Holds |
|---|---|
| `Domain` | Aggregates, events, the workflow interpreter, value objects, validators. No framework. |
| `Application` | Use cases as Mediator commands/queries + handlers, DTOs, ports (repository interfaces). |
| `Infrastructure` | The Dapr driver, KurrentDB, EF read models, Scriban, the concrete stores. |
| `Api` | gRPC services, proto ↔ domain mapping, and the module's `Add`/`Use` extensions. |

Modules: `execution` (the rich one), `definition`, `observability`. Shared kernel in `common`.

**Rules that this layout exists to enforce:**

1. **The wire format stops at `*.Api`.** `Utos.*`, `Grpc.*` and `Google.Protobuf` may be
   referenced only by a `*.Api` project and by `Common.Workflow.Proto`. Nothing deeper — not the
   application layer, not the domain, and not anything persisted. (`Execution.Infrastructure` has
   two `using Grpc.Core;` lines: those are the KurrentDB client's exception types, not proto.)
2. **`API.Main` composes `*.Api` projects only.** It never references a `*.Infrastructure`
   project. `Add{Module}Module` / `Use{Module}Module` live in `{Module}.Api/Extensions/`; each
   calls into an `Add{Module}Infrastructure` that stays in the infrastructure project.
3. **Transport translates and delegates.** A gRPC method may validate request *shape*, map
   proto ↔ domain, and turn application exceptions into `RpcException`. Anything else — resolving
   against a store, coordinating across contexts, filtering — belongs in a handler.
   A bundle's shape rules are part of that: `DefinitionGrpcService` applies
   `Utos.Workflow.Validation` to the wire form before mapping, next to where the content digest is
   computed, because both are defined over the wire form rather than over the domain model.
4. **Mediator is source-generated once, in `API.Main`.** It is the only project that sees every
   module's handlers, so `Mediator.SourceGenerator` and the single `AddMediator()` call live
   there. Application projects reference `Mediator.Abstractions` only. Adding a handler in a new
   project requires nothing beyond `API.Main` reaching it transitively — verify after adding one.
5. **Cross-cutting application code goes in `Common.Application`,** not in a module. The
   FluentValidation `ValidationBehavior` is registered globally and applies to every module's
   messages, so no module can own it. What it validates is whatever a *command* means beyond its
   bundle — the bundle's own shape was settled at the transport edge.
6. **The document's rules are the spec's, not ours.** `Utos.Workflow.Validation` implements
   `api/docs/workflow-validation.md` and is shared with the CLI, so a workflow one tool accepts
   cannot be rejected by the other. A new rule belongs in the spec and its conformance corpus,
   not in this repository. Violations are reported as a code and a path; **the code is the
   contract and the message text deliberately is not**, so assert on codes.
7. **Modules do not reference each other.** Anything two contexts must agree on lives in
   `common/` as a contract one publishes and the other consumes — `Common.Workflow.Resolution`
   (definition answers "which bundle does this reference name?") and `Common.ExecutionLifecycle`
   (execution announces that a run ended) are the whole list. Both edges this replaced compiled
   fine and read as reasonable, which is why
   `tests/UnitTests/Architecture/ModuleBoundaryTests.cs` asserts the graph instead of trusting it.

## Persistence invariants

These are easy to break silently — see the notes in `Common.Workflow` and the tests that pin them:

- **Do not recompute the bundle digest from the domain model.** `ComputeContentDigest()` is
  defined over the canonical *wire* serialization and ships inside `Utos.Workflow`. It is computed
  in `DefinitionGrpcService.LoadWorkflow` while the bundle is still proto, then passed inward as a
  string. Recomputing it anywhere else would stop matching the CLI and registry.
- **Any value object that touches persisted state must carry a `[JsonConverter]`** writing the
  same scalar the primitive wrote. `EventSerializer` serializes with no options, so the shape rests
  entirely on the converters the types carry, and a change to it makes every event written before
  it unreadable. Unreadable now *fails* — a load throws and a subscription parks the event — but
  the run is still dead either way, so the shape is a contract, not a preference.
  `tests/UnitTests/Persistence/EventJsonShapeTests.cs` pins it as text.
- **An event's stored name comes from `[EventType]`, never from its CLR type.** It used to be the
  `AssemblyQualifiedName`, so renaming an event, moving its namespace, or moving it to another
  assembly silently orphaned every event written before the move.
  `tests/UnitTests/Persistence/EventTypeRegistryTests.cs` pins the five names as literal strings —
  a rename is a failing build, not a lost stream. Two consequences: every `IDomainEvent` must carry
  the attribute or `EventTypeRegistry` throws at startup, and every name must keep the
  `utos.execution.` prefix that `WorkflowExecutionEventProcessor` filters its subscription on.
- **Changing an event's JSON shape means bumping `[EventType(Version = n)]` and registering an
  `IEventUpcaster` for the step.** The version rides in the KurrentDB event metadata (`{"v":n}`);
  absent or unreadable metadata reads as v1. Adding an optional nullable field needs neither — that
  is still the cheapest evolution, and what `AmbientEnv` and `Digest` are. A gap in the chain
  throws rather than being skipped.
- **Bundles are persisted in three places** — `WorkflowExecutionCreatedEvent.Bundle`,
  `ExecutionContexts.BundleJson`, and `WorkflowExecutions.WorkflowDefinitionJson`. All three
  change together.
- **A number's CLR type is decided in `NumberNarrowing`, and nowhere else.** Scriban dispatches
  arithmetic and comparisons on the runtime type, so this is behaviour. There are deliberately
  *two* rules: proto carries a bare double and must narrow by value (`5.0` → `int 5`), while JSON
  still has the author's token and keeps `5.0` a `double`. Do not "unify" them — collapsing to the
  proto rule turns every whole-valued decimal an API returns into an integer and changes division.
- **Nothing may deserialize workflow values without `WorkflowJson.Options`.** Without it the
  values come back as `JsonElement`, which template expressions cannot do arithmetic on, and the
  failure is silent until a template runs. The one deliberate exception is
  `HttpActivityHandler.ParseJsonResponse`: a `JsonElement` re-serializes its raw token text, which
  is the only thing preserving `10.0` as a double across the Dapr hop.

## Tests

- `tests/UnitTests` — no Docker, no sidecar, no database. Interpreter, aggregate, mapping,
  value objects, event JSON shape, event names and versions.
- `tests/IntegrationTests` — black-box gRPC clients against the real Aspire host; needs Docker.
  These are what prove the proto mapping and the service surface end to end. Validation failures
  are asserted by **rule code** (`UTOS-A005`), never by wording: the spec pins the code and leaves
  the message free to improve. Not-found message text is still matched on substrings, so that
  wording remains contract.
- `TestWorkflowBuilder` keys a bundle by canonical identity (`name:version`), not by name. It
  once keyed by name, which `UTOS-B005` now rejects — the invariant was always in `bundle.proto`,
  but nothing enforced it until the shared validator did.
