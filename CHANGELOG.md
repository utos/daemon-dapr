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
