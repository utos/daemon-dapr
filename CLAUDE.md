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
4. **Mediator is source-generated once, in `API.Main`.** It is the only project that sees every
   module's handlers, so `Mediator.SourceGenerator` and the single `AddMediator()` call live
   there. Application projects reference `Mediator.Abstractions` only. Adding a handler in a new
   project requires nothing beyond `API.Main` reaching it transitively — verify after adding one.
5. **Cross-cutting application code goes in `Common.Application`,** not in a module. The
   FluentValidation `ValidationBehavior` is registered globally and applies to every module's
   messages, so no module can own it.
6. **Modules do not reference each other.** Anything two contexts must agree on lives in
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
  same scalar the primitive wrote. `EventSerializer.DeserializeEvent` catches every exception,
  logs, and returns `null`, and the repository then drops that event while still counting its
  stream revision — so a changed JSON shape does not fail loudly, it rehydrates aggregates with
  holes. `tests/UnitTests/Persistence/EventJsonShapeTests.cs` pins the shape as text.
- **Bundles are persisted in three places** — `WorkflowExecutionCreatedEvent.Bundle`,
  `ExecutionContexts.BundleJson`, and `WorkflowExecutions.WorkflowDefinitionJson`. All three
  change together.

## Tests

- `tests/UnitTests` — no Docker, no sidecar, no database. Interpreter, aggregate, mapping,
  value objects, event JSON shape.
- `tests/IntegrationTests` — black-box gRPC clients against the real Aspire host; needs Docker.
  These are what prove the proto mapping and the service surface end to end. Several assert on
  `RpcException.Status.Detail` substrings, so validator and not-found message text is contract.
