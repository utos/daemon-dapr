# Changelog

All notable changes to the Utos daemon are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `ExecutionService.DeleteExecution` now deletes a terminal execution's record — both its read-model
  row and its `workflowexecution-{id}` / `subworkflowcalls-{id}` event streams (soft delete), so a
  projection rebuild cannot resurrect it. Deletion cascades to the execution's non-detached
  sub-workflow records; detached sub-workflows are independent top-level executions and are left
  untouched, continuing to run. A `SCHEDULED` or `ACTIVE` execution returns `FAILED_PRECONDITION`
  (it must be cancelled first) and an unknown id returns `NOT_FOUND`. Previously the RPC was
  unimplemented and returned `UNIMPLEMENTED`.
- Detached (fire-and-forget) sub-workflows via `WorkflowActivityConfig.detached`. When set, the
  executor starts the sub-workflow as its own independent, top-level execution instead of awaiting
  it as a Dapr child: the parent transitions immediately with a `{ "execution_id": "<id>" }` handle
  as the activity output, and the detached run gets its own execution id, log stream, and lifecycle,
  appears in `ListExecutions`, and may outlive the parent.
