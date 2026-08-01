# Changelog

All notable changes to the Utos daemon are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Removed

- Six sub-workflow and promise lifecycle event types (`SubWorkflowCreated/Started/Completed/Failed`,
  `PromiseGroupStarted/Resolved`), the `SubWorkflowCalls` table and its projection, and the
  `subworkflowcalls-{id}` stream. Nothing ever emitted these events, so the projections that
  declared handlers for them could not fire, the table was always empty, and the stream was only
  ever deleted and never written — but the wiring read as working behaviour. They belonged to a
  design in which an awaited sub-workflow was an execution in its own right; the daemon instead
  runs one as a Dapr child orchestration with no separate identity, while a detached sub-workflow
  becomes a genuine top-level execution. No observable behaviour changes.

### Changed

- The three modules no longer reference each other. Two project references survived the earlier
  layering work and were the last of their kind: `Observability.Infrastructure` pulled in
  `Execution.Domain` so one handler could pattern-match `WorkflowExecutionCompletedEvent` and
  `WorkflowExecutionFailedEvent`, and `Execution.Application` pulled in `Definition.Domain` so
  `ScheduleExecutionHandler` could call `IWorkflowDefinitionStore.Resolve`. Each was a single file
  reaching for another context's types because they were one `using` away.

  Both relationships are now contracts in `common/`, and both modules on either side reference the
  contract instead of each other. `Common.Workflow.Resolution` holds `IWorkflowResolver` —
  "which loaded definition does this reference name?" — which the definition module answers with a
  small adapter over its store; execution asks the question without knowing a store exists.
  `Common.ExecutionLifecycle` holds `IExecutionLifecycleListener`, on which execution announces
  that a run reached a terminal state; observability's stream-completion handler becomes a
  listener and moves up into `Observability.Application`, because it stopped reading another
  context's events to find out what happened and is now simply told. The two domain events stay
  exactly where they are — they are the aggregate's own, and relocating them into shared space
  would have recreated the leak under a different project name — so nothing about the event
  stream or its JSON changes.

  Two contracts projects rather than one shared `Common.Contracts`: a single project would have
  needed both `Common.SeedWork` and `Common.Workflow`, leaking the workflow *document* model into
  observability, which has no business knowing what a workflow document is. Rejected alternatives
  worth recording — an adapter in `API.Main`, which cuts the reference but puts unowned, untestable
  cross-context translation in the composition root; and a port in `Execution.Application`
  implemented by `Definition.Infrastructure`, which reverses the edge rather than removing it, and
  points the module that owns definitions at one of its consumers.

  **The coupling moves from the compiler to the container rather than disappearing.**
  `ScheduleExecution` still fails if `AddDefinitionModule` was never called; the error now names
  `IWorkflowResolver`. What is gained is that the dependency is a contract with a name and a
  doc comment instead of a reach into another context's internals, that the modules compile
  independently, and that a test can defend it. `ModuleBoundaryTests` reads the project files —
  not compiled metadata, which drops references nothing uses — and fails on any module-to-module
  edge. `Definition.Infrastructure` also gains the `AddDefinitionInfrastructure` extension the
  other two modules already had; its store was registered inline in `Definition.Api`, the one
  place the layering rule was not being followed.

  The gRPC contract, the database schema and the event-stream format are all unchanged.

- Workflow-document validation moved out of the execution module into a new shared-kernel project,
  `Common.Workflow.Validation`. `WorkflowBundleValidator` and the validators below it lived in
  `Execution.Domain`, and the only registration of `IValidator<WorkflowBundle>` was in
  `AddExecutionInfrastructure` — but `Definition.Application`'s `LoadWorkflowHandler` injected it.
  Definition had no compile-time path to that type and worked only because `Program.cs` happened to
  add the execution module first; `AddDefinitionModule` alone failed at resolve time, and no test
  covered the definition side of validation. The rules describe the workflow *document*, so they now
  sit beside the model in `common/` — the same split as `Common.Workflow.Proto` — and each module
  asks for them itself via `AddWorkflowValidation()`. `ReservedKeywords`, `PromiseMode(s)` and
  `HttpUrlRules` move to `Common.Workflow` for the same reason: they are the document's vocabulary,
  not execution machinery.

  Loading a definition now validates through the same pipeline as scheduling an execution: a
  `LoadWorkflowCommandValidator` replaces the hand-rolled check in the handler, so
  `WorkflowBundleInvalidException` is gone and `LoadWorkflow` reports an invalid bundle as
  `InvalidArgument` from `ValidationException`, as `ScheduleExecution` already did. Three dead
  registrations (`IValidator<Workflow>`, `WorkflowActivityValidator`, `WorkflowMetadataValidator` —
  never injected, since their parents construct them directly) are removed.

- The layers are now named for what they are — **Domain / Application / Infrastructure / Api** —
  and every module has all four. "Features" previously meant three different things: in Execution
  it was a real application layer (commands, queries, handlers) that *also* hosted the gRPC
  service; in Observability it was an entire project containing one gRPC service and nothing else;
  in Definition it did not exist, and the gRPC service sat in `Infrastructure`. The `Grpc.AspNetCore`
  package followed whichever project the service had landed in, which is the tell that the split
  described where a class happened to be rather than a rule.

  `Execution.Features` is now `Execution.Application`; `Observability.Features` is now
  `Observability.Application` and has real use cases instead of a service; and
  `Definition.Application` is new. The three gRPC services move into new `Execution.Api`,
  `Definition.Api` and `Observability.Api` projects, together with the proto mapping that
  accompanies them, so **no project below `*.Api` references a `Utos.*` or `Grpc.*` package** —
  the wire format now stops at the transport edge in the project graph, not just by convention.
  `API.Main` composes the three `*.Api` projects and no longer references any `*.Infrastructure`
  project.

  Transport handlers became translate-and-delegate. `ScheduleExecution` no longer resolves the
  definition itself — it reached across into another context's store from inside a gRPC method —
  so `ScheduleExecutionCommand` now carries a `WorkflowReference` and its handler resolves it,
  while the existing bundle-carrying command is renamed `StartExecutionCommand` and remains the
  path the detached sub-workflow activity uses (it holds the parent's snapshotted bundle and never
  consults the store). `WatchExecution`'s existence check, filtering and level mapping move into a
  `WatchExecutionQuery` handler behind a narrow `IExecutionProbe` port, so the observability side
  no longer needs `IEventStoreRepository` or knowledge of stream names. `DefinitionGrpcService`'s
  four operations become commands and queries; the bundle digest is still computed at the edge,
  on the wire form, because it is defined over the canonical serialization.

  Mediator is now source-generated once in `API.Main`, the only project that sees every module's
  handlers, and the shared `ValidationBehavior` moves to a new `Common.Application`. `AddGrpc()`
  moves to the host too — it was called only by the execution module, which the other two silently
  depended on. Behaviour, the gRPC contract, the database schema and the event-stream format are
  all unchanged.

- An execution is now identified by an `ExecutionId` rather than a raw `Guid`. The id appeared as
  a bare `Guid` in 64 files — the aggregate, every lifecycle event, the Dapr activity inputs
  (where an execution's own id and the id its logs are routed under sat side by side as two
  interchangeable `Guid` parameters), the read model, and the log store — and was re-parsed from a
  string at each of the three gRPC entry points. `AggregateRoot` and `Entity` became generic in
  their identifier so the shared kernel does not have to assume one, and the duplicate stream-name
  formatter in `EventStoreRepository` was folded into `StreamNames`. The gRPC contract is
  unchanged (an id is still a GUID string on the wire) and so is the database schema: the id
  keeps its `uniqueidentifier` columns through a single convention-registered value converter, and
  its JSON converter writes the same bare GUID string a raw `Guid` did, so events already in the
  stream and Dapr payloads already in flight keep deserializing. A new `EventJsonShapeTests`
  asserts that shape as text, because the event store logs and drops an event it cannot
  deserialize rather than failing — a changed shape would have truncated an aggregate's history
  silently rather than erroring.

- Workflow identity and semantic version are now types rather than static helpers over strings.
  `WorkflowIdentity` was a static class that could only *format* `[registry/][namespace/]name:version`,
  and semver was an `IComparer<string>` with its parser hidden in a private method — so a resolved
  definition was carried around as a `WorkflowReference` whose version and digest were both
  nullable, with a doc comment asserting the invariant the type did not enforce. Both are now real
  value objects in `Common.Workflow`, with parsing, comparison and equality, so the definition
  store keys on the identity itself instead of on a formatted string. Two duplicate encodings of
  the semver grammar collapse into one (a regex in `WorkflowMetadataValidator` and a hand parser
  in `SemanticVersionComparer`, in projects that did not reference each other), as do the five
  copies of the "leave optional reference fields unset rather than empty" rule. `WorkflowReference`
  remains a separate type for a partial *request*, where an omitted version still means "latest".
  The gRPC contract, the database schema and the event-stream format are all unchanged.

- The daemon now has its own workflow model instead of using the generated protobuf types as its
  domain model. `Utos.Workflow.V1` / `Utos.Daemon.V1` types previously reached every layer: the
  `WorkflowExecution` aggregate's status *was* the wire enum, `WorkflowExecutionCreatedEvent`
  embedded a whole `WorkflowBundle` proto and persisted it into the event stream, and a transport
  `WorkflowReference` was a repository query parameter. That coupled both the domain model and the
  durable event history to the wire contract, so a spec version bump could break event
  deserialization. The new `Common.Workflow` model is plain C# and serializes with
  `System.Text.Json` directly, replacing the proto `JsonFormatter` round-trip the daemon needed in
  three separate places (the event stream, `ExecutionContexts.BundleJson`, and the read model's
  `WorkflowDefinitionJson`). Protobuf is now confined to the transport edge — the gRPC services and
  a single mapping layer, `Common.Workflow.Proto` — and neither `Execution.Domain` nor
  `Definition.Domain` references a `Utos.*` package at all. The gRPC contract is unchanged: the
  mapping is lossless in both directions, including proto3 optional unset-vs-empty and spec
  dependencies, so a bundle read back through `GetWorkflow` or `GetExecutionDetails` is identical
  to the one loaded.

  **This is a breaking change to persisted state.** Bundles were previously written as protobuf
  JSON and are now written as the domain model's own JSON, so existing `workflowexecution-{id}`
  event streams cannot be replayed and existing execution rows cannot be read. Drop the KurrentDB
  volume and the SQL database before running this version. There is no upcaster; the daemon is
  pre-1.0 and the read model was already rebuilt from scratch on startup.

### Fixed

- A "workflow definition not found" error for a reference without a version no longer renders a
  dangling separator. Asking for `send-email` with no version reported `Workflow definition
  'send-email:' not found`, which reads as a daemon defect rather than as "that workflow isn't
  loaded"; a reference now renders only the segments it carries.
- A detached sub-workflow now records its own namespace and registry. Its top-level execution row
  was built from a reference constructed with just name and version, so `ListExecutions` could not
  be filtered by namespace or registry for detached runs, and the reference echoed back omitted
  both.
- An execution's full lifecycle is now written through the `WorkflowExecution` aggregate, which is
  the only writer to its event stream, and appends are guarded by the stream revision the aggregate
  was loaded at. This fixes three defects. An execution that received a duplicate `Scheduled` event
  could no longer be loaded at all — the invariant guarding that transition ran during replay as
  well as on the command, so the aggregate rejected its own history. A duplicate or late terminal
  write could record both `Completed` and `Failed` on one execution, reachable whenever a terminal
  write succeeded but its acknowledgement was lost and the retry surfaced as a failure; the first
  terminal state now wins and later transitions are ignored. And a failure reported without a
  reason produced a null `WorkflowError.message`, which now reads as an explicit "failed without a
  reported reason". Lifecycle event types and payloads are unchanged, so read models, the
  `WatchExecution` terminal frame, and `DeleteExecution`'s terminal-state precondition all behave
  as before.
- A promise branch now inherits the run's ambient `env`, so `{{ env.x }}` resolves inside the
  workflow a branch runs just as it does inside an awaited sub-workflow. Previously each branch's
  execution context was built without the parent's env, so any activity in a branch that
  referenced `env` failed with an undefined-variable error mid-run. Env used in a branch's own
  input mapping was unaffected — the parent renders that before the branch starts — so this only
  showed up once a branch's own activities reached for it.

### Added

- `ObservabilityService.WatchExecution` now emits a structured terminal frame just before the
  stream closes: a final `WatchExecutionResponse` with `status` set to `COMPLETED` or `FAILED`,
  and — on failure — an `error` (`WorkflowError` with code `EXECUTION_FAILED` and the failure
  reason as its message). Previously these fields were never populated and a watching client could
  only infer failure by scraping error-level log text before the stream closed. The terminal frame
  respects the request's level/source/category filters like any other frame (a `COMPLETED` frame is
  Info-level, source `system`), so an unfiltered watcher always receives it.
- `ExecutionService.ListExecutions` now honors the `ListExecutionsRequest.workflow` filter. When set,
  `name` selects the workflow (required — an empty name returns `INVALID_ARGUMENT`) and each further
  populated field of the `WorkflowReference` narrows the results: an omitted `version` matches all
  loaded versions, and `namespace`, `registry`, and `digest` match exactly when provided. To support
  digest/namespace/registry filtering, an execution now persists its full resolved workflow identity
  (the bundle content `digest` is captured on the creation event; namespace and registry are projected
  from the entry-point metadata), and execution summaries/details echo these fields back. Executions
  created before this change have no stored digest/namespace/registry and are not matched by a filter
  that specifies one of those fields. Previously the request was ignored and every execution returned.
- `ExecutionService.DeleteExecution` now deletes a terminal execution's record — both its read-model
  row and its `workflowexecution-{id}` event stream (soft delete), so a projection rebuild cannot
  resurrect it. There is nothing to cascade to: an awaited sub-workflow is a Dapr child
  orchestration rather than an execution of its own, so it has no record to delete, and a detached
  sub-workflow is an independent top-level execution that is left untouched and continues to run.
  A `SCHEDULED` or `ACTIVE` execution returns `FAILED_PRECONDITION` (it must be cancelled first)
  and an unknown id returns `NOT_FOUND`. Previously the RPC was unimplemented and returned
  `UNIMPLEMENTED`.
- Detached (fire-and-forget) sub-workflows via `WorkflowActivityConfig.detached`. When set, the
  executor starts the sub-workflow as its own independent, top-level execution instead of awaiting
  it as a Dapr child: the parent transitions immediately with a `{ "execution_id": "<id>" }` handle
  as the activity output, and the detached run gets its own execution id, log stream, and lifecycle,
  appears in `ListExecutions`, and may outlive the parent.
