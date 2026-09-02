# Changelog

All notable changes to the Utos daemon are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Requirements

- **Dapr runtime 1.18.4 or later is now required**, up from 1.18.3. An `on_emitted` handler is a
  child execution, and the driver mints its id with `WorkflowContext.NewGuid`. On earlier runtimes
  that counter restarts with the history, so the first id minted after a continue-as-new is the one
  the previous segment minted — and a consumer's producer survives every continuation by design, so
  the ids collide while the earlier child is still live. Dapr then declines to start the duplicate
  and **neither faults nor completes the parent's task**: the consumer waits forever on a rendezvous
  that cannot happen, with one debug line and nothing else. Any consumer that outlives
  `Execution:ContinueAsNewAfterTransitions` hits it, which is every long-running one.
  Three upstream changes make it correct rather than one: `dapr/durabletask-dotnet` seeds the guid
  namespace from the execution id, `dapr/durabletask-go#130` populates that id on the work item
  (it was hardcoded `nil`, so the seeding had nothing to read), and `dapr/dapr#10400` makes a
  genuine collision fail loudly instead of hanging. Only the second is strictly needed here; the
  third is why a future collision from any other source is diagnosable. Released together in
  **1.18.4** and verified against `1.18.4-rc.3`.
  The daemon carried a workaround for one release — handler ids derived from durable state rather
  than from `NewGuid` — which is now removed. It is not kept as a belt-and-braces measure because
  it would mask exactly the failure this requirement exists to make visible.

### Breaking

- **Dispatched work is its own document** (spec `v0.0.13`, `Utos.Workflow` pins bumped `0.0.12` →
  `0.0.13`). A promise branch and an `on_emitted` rule name a *document* — `workflow`,
  `startActivity`, `input` — rather than pointing at an activity in the dispatching one.
  `PromiseBranch.Target` is gone and `CallActivityConfig.OnEmitted` holds `DispatchRule` instead of
  `TransitionRule`, which changes the persisted shape in all three places a bundle is stored, so
  **existing streams cannot be replayed — drop the KurrentDB volume and the SQL database.** There is
  no upcaster; the daemon is pre-1.0. `ActivityConfigJsonShapeTests` pins the new text.
- **A handler can no longer stop its consumer.** It used to: a handler transitioning to `end` ended
  the consuming run, which was how a consumer stopped early. A handler is a separate execution now,
  so `end` ends the handler and the consumer takes the next value. Nothing dispatched can reach into
  the flow that dispatched it — that is the property the document boundary exists to provide — but
  it means a consumer runs until its producer terminates, with no way to say "enough" based on what
  it consumed. `A_handler_that_ends_finishes_only_itself` records the gap rather than hiding it.
  Closing it needs a spec decision, not a daemon workaround.

### Fixed

- **A child started after a continue-as-new could collide with a live one, and hang the run.**
  `WorkflowContext.NewGuid` is replay-safe but not continuation-safe: its counter restarts with the
  history, so the first id minted in the second segment is the same one the first segment minted.
  Harmless when that child has finished, fatal when it has not — and a consumer's producer survives
  every continuation by design. Dapr declines to start an instance whose id already exists, so the
  handler never ran and the consumer waited forever on a rendezvous that could not happen. Nothing
  threw; the trace simply stopped. Handler ids now come from `DeterministicExecutionId` — a pure
  function of (execution, activity, dispatch sequence) — with the sequence carried in the
  continuation envelope, so they replay identically and cannot repeat across segments.

### Changed

- **A promise branch name is rendered**, so a `for_each` expansion can be named after the item it
  processes. The proto has documented `{{ alias.x }}` here since the field existed, and the
  interpreter never honoured it — names were built from the raw string plus `_{index}`, so the
  shipped conformance fixture produced keys of the literal text `addon-{{ item.sku }}_0`. The
  ordinal survives as a **fallback**, applied only when the rendered names within one expansion are
  not distinct: a literal name renders to itself every time, so dropping it outright would have made
  every existing `for_each` collide — at run time, on documents the shared validator accepts, which
  is the divergence this whole change exists to close.
- **An `on_emitted` handler completes one iteration by finishing.** Consuming was a loop the author
  had to close by hand, transitioning to the call activity by name from the end of the handler.
  Omitting that ended the subscription, cancelled the producer and completed the run — so a watcher
  written to poll for weeks exited after one value and reported success. The handler is the body of
  the loop and now behaves like one; being a document, what "finishing" means is its execution
  terminating, which the consumer learns of by the child ending.
- **A rule list where nothing matches skips that value.** The same rule, and the case that made a
  filtering consumer unwritable: conditions that all miss exhaust the list, which used to mean the
  run was over. One uninteresting value shut the producer down.
- Writing the back-edge by hand is **no longer meaningful, and is now harmful**. A handler is a
  document, so transitioning to the call activity re-enters it *inside the handler* and opens a
  second producer — recursively. `DispatchStack` and `EvaluateTransitionInput.DispatchedBy` are
  deleted with it: the driver no longer needs to know which handler a path is under, because a
  handler path cannot be in this execution at all.
- **A promise branch names a document, and `workflow: self` covers the current one.** It used to
  have to name a `workflow.call` activity, so fanning out over an HTTP request meant wrapping it in
  a second document that did nothing else — and the daemon threw on branch targets the shared
  validator accepts, which is the one thing the two must not do. Both are gone: a branch says which
  document to run and where to enter it, and `self` is what the CLI resolves to this document's own
  identity before a bundle exists. The daemon never sees the word.
- `CancelSubscribedChildActivity` is now `CancelChildExecutionActivity`. It has two callers rather
  than one — an abandoned subscription and a promise branch nobody is waiting on — and "subscribed"
  was only ever true of the first. An orchestration in flight across the upgrade that is inside a
  promise fan-out cannot resume regardless: the settle path now schedules cancellations that its
  recorded history does not contain.

### Added

- **`on_failure` is executed.** It was specified, validated, authored in the shipped examples and
  present in the conformance corpus — and read nowhere, so every `onFailure` block written to date
  validated cleanly and did nothing. Rules are now evaluated in order, first match wins, exactly as
  `on_success`. The asymmetry is the part to remember: on the success path an unmatched rule list
  means the run finished, while on the failure path it means the error was never handled and must
  keep travelling. `TransitionDecision.Propagate` exists so those two cannot be confused — folding
  them together would report a failed run as Completed, which no existing test would have caught.
- **`error` template context** — `code` and `message`, on the failure path. Deliberately not folded
  into `output`: a failed activity produced no output, and overloading the name would let a
  condition written for the success path silently read error fields on the failure path. Codes stay
  coarse (`EXECUTION_FAILED`) until the pipeline records structured causes; a rule branches on
  `response.status`, which is where the detail actually is.
- **`response` template context** — `status`, `headers`, `bodyText` for an http activity, on **both**
  paths. This is what lets a failure rule tell a 429 from a 401. `bodyText` is always present, and
  is the only way to read a non-JSON body at all: `output` is null unless the content type is
  exactly `application/json`, so `text/plain`, `text/csv` and `application/vnd.api+json` responses
  were previously unreadable.
- Both new contexts, and every key inside them, are **always defined**, with nulls where they do not
  apply. An empty context is not enough: the template engine throws on a missing *member*, not only
  on an undefined root, so `response.status == 429` on an activity that made no request — or a
  request that timed out — would otherwise raise *while a failure is being handled*.
- Egress policy for workflow HTTP (`HttpEgressOptions`). Link-local (`169.254.0.0/16` and the IPv6
  equivalents) is denied with no override, because it is the cloud instance-metadata range and
  reaching it turns "can author a workflow" into "holds the cloud account's credentials". Loopback
  and private ranges stay allowed by default, since denying them would break every self-hosted
  daemon calling an internal API. Enforced in `SocketsHttpHandler.ConnectCallback` rather than a
  `DelegatingHandler`: auto-redirect means a message handler never sees the redirected hop, and
  resolving then dialling the same approved address leaves no window for DNS rebinding.

### Fixed

- **A promise now cancels the branches it did not wait for.** `SettleAsync` stopped awaiting once
  the mode was satisfied and simply dropped whatever was left in `remaining` — but a branch is a
  child orchestration, not a task, and dropping the task stops nothing. The loser of a `race`
  between two mirrors ran to completion, issued its requests and wrote its output entries, spending
  everything the branch was going to spend, with the parent already several activities further on.
  The same on the failure path: `all` fails fast, and its siblings carried on after the run had
  already failed. This is the class of bug `AbandonSubscriptionsAsync` exists to fix for streaming
  producers; branches never got the equivalent. They do now, on every path and before the promise
  returns — a satisfied mode, a branch's own failure, or a `count` that enough failures have put
  out of reach — so a settled promise means no branch of it is still running, which is what a
  caller would assume anyway. Cancellations are ordered by branch name for the same reason the
  subscription sweep is: each one is a history-recorded activity call, and an unordered sweep
  replays in a different order than it ran. A branch that finishes inside the settle/cancel race is
  not an error — termination already treats a missing or finished instance as an ordinary outcome.
- **Routing a failure to the reserved `error` keyword now fails with the cause.** It reached the
  navigator, which fails the run with a hardcoded "Workflow transitioned to 'error'" and discards
  what went wrong — making the explicit fallback strictly worse than omitting it, since declaring
  no matching rule propagates the original exception. Adopting `on_failure` therefore made failures
  *harder* to diagnose than leaving it alone. On the failure path `error` now rethrows the captured
  exception, so stating "fail the run" and saying nothing mean the same thing. The keyword keeps its
  own meaning on the success path, where it turns an acceptable response into a failure and there is
  no cause to carry.
- **Workflow HTTP was using an unconfigured `HttpClient`.** `ExecuteActivity` resolves
  `IEnumerable<IActivityHandler>`, which bound to the `AddScoped<IActivityHandler, …>` registration
  and received the default unnamed client, while `AddHttpClient<HttpActivityHandler>()` configured a
  named client nothing resolved. Any timeout or handler attached there would have been silently
  inert. Collapsed into `AddHttpClient<IActivityHandler, HttpActivityHandler>()`.
- **An author-declared `content-type` is honoured.** Every body was sent as `application/json`
  because `Content-Type` is a content header and `request.Headers.TryAddWithoutValidation` silently
  drops it. `application/x-www-form-urlencoded` was therefore inexpressible, which made a standard
  OAuth token refresh impossible — and so made any long-running workflow that must renew its own
  credential impossible with it. Defaults to `application/json`, so existing workflows are unchanged.
- **Resilience is on again, minus retry for workflow HTTP.** The standard handler had been commented
  out in `ServiceDefaults` to keep retries away from workflow activities, but
  `ConfigureHttpClientDefaults` applies to every client in the process, so that also stripped
  timeouts and circuit breaking from the daemon's own internal calls. It is restored, and the
  workflow client opts out on its own with `RemoveAllResilienceHandlers()` plus a timeout-only
  pipeline. Retry is wrong *there* specifically: a workflow's POST may create an order or charge a
  card, there is no idempotency key to make a second attempt safe, and with `on_failure` the author
  is now the one who decides whether to retry.
- Workflow HTTP has an explicit timeout (30 s, configurable) instead of the `HttpClient` default of
  100 s. Uses the Polly strategy so a timeout raises `TimeoutRejectedException` rather than the
  `TaskCanceledException` a genuine cancellation also raises.
- A non-2xx no longer throws from inside the activity. An exception flattens to a message string at
  the Dapr boundary, taking the status and headers with it; the handler now reports the failure and
  the executor raises it, which is what keeps `response` available to a failure rule.
- A failure-path transition with no `input` transform passes the **failing activity's own input**
  through. It previously fell through to the activity's absent output, which
  `WorkflowSession.InputFor` then replaced with the *run* input — silently rewinding a handler to
  the state the workflow started in.

- **Every execution has a durable output stream.** Ordered, cursor-addressable, its own KurrentDB
  stream (`executionoutput-{id}`), surviving daemon restart and orchestrator replay — see
  `api/docs/execution-output-stream.md`. A stream is zero or more `value` entries followed by
  exactly one terminal entry (`result` or `error`), which is what makes the ordering between what
  a workflow emitted and what it returned arithmetic rather than a rule: one cursor walks one log.
- **The `emit` transition action** — append a value to this execution's output stream and carry
  on. `result` is emit-and-terminate; `emit` is emit-and-continue, so a workflow can produce values
  as it goes rather than only once, at the end. Its `transition` is required: an emit is not a
  terminal action, so a rule that emits and goes nowhere is a dead end rather than a return
  (`UTOS-T004`).
- **`ExecutionService.WatchOutput`** — stream an execution's output from a cursor, `after` or
  `tail`. Deliberately separate from `ObservabilityService.WatchExecution`, which carries log
  lines: emitted values are what a workflow produced, not a record of what it did, and folding
  them into a filterable log stream would let a `level` filter drop them. Watching a finished
  execution replays its recorded entries and then ends, and a cursor already at the terminal entry
  returns nothing rather than blocking on an entry that can never arrive.
- Readers here are **observers**: reading never gates the producer, however far behind the cursor
  falls. Gating belongs to a `workflow.call` parent that declared `on_emitted`, which does not
  exist yet. `WatchOutput` on a sub-workflow's id reports NOT_FOUND: a `workflow.call` child has
  no execution record, and its stream exists for its caller rather than for clients.
- **`GetExecutionResponse.result`** — what a run returned, closing a gap that predates this work:
  there was previously no result field anywhere in `daemon/v1`, so a completed execution's value
  had nowhere to go over the wire. It is read back from the terminal entry of the output stream
  rather than projected into the read model — the stream is already where a result lives, and a
  copy in a projection would be a second thing to keep in step, one the lifecycle event it is
  built from does not even carry. Reading it the moment the status says Completed is safe because
  the terminal entry is appended before that lifecycle event. Set for every completed run,
  **empty included**: a run that ended without returning did produce a result, and reporting that
  as an absent field would make it indistinguishable from one that failed.
- The terminal entry is written by whoever ends the run — the driver on success and failure, and
  `CancelExecutionHandler` on cancellation, since cancelling tears the orchestration down and
  nothing else would ever close the stream. It carries `EXECUTION_CANCELLED` rather than
  `EXECUTION_FAILED`: a reader of the stream sees the entry and not the run's status.
- `DeleteExecution` deletes the output stream alongside the lifecycle stream.
- **Sub-workflow output streams are reclaimed**, at both ends a run can stop at. A
  `workflow.call` child writes its own output stream — the consumer side reads it — but is a child
  orchestration rather than an execution of its own, so it has no record to delete it by. The
  caller drops the stream as each call returns, which is what bounds a parent that loops over a
  sub-workflow call and may never terminate at all; the terminal transition reclaims whatever did
  not get there, such as the child a run failed on. Cancellation reclaims too, since tearing the
  orchestration down means the driver's terminal transition never runs.
- `ExecutionContext.RootExecutionId`, with a migration — the only record anywhere that a
  sub-workflow execution happened, and therefore the only way to find what it left behind. The
  *root* rather than the immediate parent, so one query reaches a whole tree rather than one
  level. Streams are always deleted before the rows that name them: the other order does not leak
  a deletion, it makes the streams unreachable.
- **Fixes a pre-existing leak this uncovered:** `IExecutionContextStore.DeleteAllAsync` deleted a
  single row despite its name, so every sub-workflow's context row outlived the run that created
  it, and a cancelled run's own row was never dropped at all — the driver that would have dropped
  it had been terminated.
- **`ExecutionService.CancelExecution`** — stop a running execution, optionally with a reason. The
  run reaches the new terminal status `Cancelled` and its orchestration is torn down. This closes
  a hole the daemon already depended on: `ExecutionLimitsOptions` notes that an endless loop
  "would be unstoppable short of purging the Dapr instance", and `DeleteExecution` told callers to
  cancel first without there being any way to.
- Cancellation is **idempotent, and loses to a state the run already reached**. Cancelling an
  already-cancelled execution succeeds; one that finished under the caller's feet reports
  `FAILED_PRECONDITION` rather than a success implying the cancel did something. The first
  terminal state wins, consistently with every other lifecycle transition, because the driver is
  at-least-once.
- `IExecutionRuntime` — the orchestration as a port, so cancelling a run does not pull Dapr into
  the application layer, the way `IExecutionContextStore` keeps the database out of it.
- `CancelledAt` and `CancellationReason` on the execution read model, with a migration.
  `CancellationReason` is **its own column rather than `ErrorMessage`**: a run stopped on purpose
  has not errored, and a client should not have to read the status to know how to interpret a
  field. `finished_at` now derives from the cancellation time too, so a cancelled run reports when
  it stopped.
- **`on_emitted` — a caller consumes a sub-workflow's output stream as it is produced**, rather
  than only its one terminal value. `CallActivityConfig.OnEmitted` makes the caller that child's
  privileged consumer, and every value is a rendezvous: the child appends its entry, hands the
  value over, and parks until the caller says it has been consumed, so the unconsumed buffer never
  exceeds one entry however much faster the producer is. The handler path must transition back to
  the call activity — that is both the acknowledgement and the request for the next value — and
  leaving it any other way ends the subscription and cancels the child.
  Two decisions are load-bearing and easy to undo by accident. The parent waits on **events, not
  on the child's task**: a task cannot survive re-entering the activity, nor a continue-as-new,
  and the subscription must survive both. And on the child's failure path the terminal message is
  raised **before** the entry is appended — the one inverted ordering in the class, because a
  consumer is blocked on that message and the append is a step that can throw and strand it.
  A caller that declares no handler is unaffected: the child never waits, and only the terminal
  value crosses. `UnconsumedEmissionTests` pins that ungated contract.

### Changed

- **The loop ceiling is gone** — the setting, the counter and the failure mode, not just the
  default. `Execution:MaxLoopedTransitions` is removed; a loop with no exit now runs until it is
  cancelled, which is what the spec always said it should do. The ceiling was a stand-in for a
  cancellation primitive that did not exist, its own comment said so, and keeping it as a knob
  would have left a config setting whose only purpose is to reintroduce a bug. A workflow
  consuming another's output stream re-enters its call activity once per emitted value, so any
  finite ceiling was a countdown to killing a long-lived watcher for doing its job.
  `ExecutionCursor` loses `LoopedTransitions` with it: the count existed to be compared against
  the ceiling, and the one thing still needed across a continuation — *whether* a loop was
  detected — is now carried as the boolean it always was.
  Note what this does **not** loosen. Orchestration history is bounded by
  `ContinueAsNewAfterTransitions`, which is untouched and is the limit that was ever load-bearing:
  history is state Dapr must rebuild after a crash or a node move, while an output stream is a
  ledger that is appended to and read by cursor, with nothing to rebuild.
- `DeleteExecution` accepts `Cancelled` as a deletable terminal state.
- **The cascade to awaited sub-workflows needed no code.** They are child orchestrations of their
  parent rather than executions of their own, so Dapr tears them down with it — which is exactly
  the spec's rule. A `workflow.spawn` child is a separate top-level orchestration and is
  deliberately left running.
- **Sub-workflow invocation is two domain types rather than a flag.** `SubWorkflowActivityConfig`
  becomes an abstract base over `CallActivityConfig` and `SpawnActivityConfig`, matching spec
  `0.0.12`. The old `Detached` boolean decided what the activity *returned* — the child's result
  or a `{execution_id}` handle — and whose failures `on_failure` saw, so it could not be reasoned
  about at the point that output is consumed. The orchestrator still matches on the shared base
  wherever the distinction does not matter.
- **Promise completion is the `PromiseMode` enum end to end.** The wire format models it as a
  oneof, so `PromiseModes` — the string table that parsed a mode name — is deleted along with the
  interpreter's parse step and its "unsupported promise mode" failure: an unknown mode is no
  longer representable. `RequiredCount` is carried only by the count mode, so it can no longer be
  set where it is ignored. The coverage that lived in `PromiseExpansionTests` moved to
  `WorkflowMappingTests`, which is where choosing the mode now happens.
- Mapping **refuses an unset mode** on either construct rather than defaulting. `UTOS-A007`
  rejects these at load, and guessing would silently change what an activity returns, or when it
  resolves.
- Bumped the Utos SDK packages to `0.0.12`.
- **Cancelling waits for the orchestration to actually stop.** Dapr's `TerminateWorkflowAsync`
  completes once the termination has been *enqueued*, which says nothing about whether anything
  stopped — while `CancelExecutionHandler` goes straight on to write the run's terminal output
  entry and delete the execution context rows its activities read on every step. `TerminateAsync`
  now confirms a terminal status before returning, which is what makes that assumption true, and
  passes the caller's cancellation token so an abandoned request no longer leaves the daemon
  waiting on a call nobody wants. How long a cancellation may take stays the caller's policy:
  the wait is bounded by that token and by nothing this repository invents.
  It cannot promise more than it says. Dapr lets in-flight activities run to completion after an
  instance is terminated, and a queued work item can still be dispatched, so "terminal" means the
  orchestration will schedule no more work — not that nothing is still touching the run's state.

### Added

- `ActivityConfigJsonShapeTests` pins the polymorphic type discriminators activity configs persist
  under. They are a persistence contract — `EventSerializer` swallows a deserialize failure, logs,
  and returns null, after which the repository drops the event while still counting its stream
  revision, so a renamed discriminator rehydrates an execution with a hole rather than failing.
  **Nothing pinned them before**, which is how `detached` could be replaced without a test
  noticing.
  The same suite records two pre-existing surprises it turned up rather than silently inheriting
  them: `Kind` reaches the stream despite carrying `[JsonIgnore]` (the attribute sits on the
  abstract declaration, which System.Text.Json does not honour for the derived overrides), and
  `HttpActivityConfig.HeaderMap` — a computed convenience property — lands there too. Both are
  write-only and harmless, and both are now visible.
- **Every gRPC call the integration suite makes now carries a deadline**, applied by an
  interceptor on the fixture's channel rather than at two hundred call sites — 30s for a
  request/response call, 120s for a server stream, and a call that sets its own keeps it. Without
  one, a daemon that stopped answering was indistinguishable from a hung run: the call never
  returned, so the polling helpers that wrap it never got back round to checking their own
  timeouts, and the suite died with no test named. That is why an intermittent cancellation fault
  took a session to locate. Four stream timeouts of 300–3000 seconds — long enough to be no
  timeout at all — came down to 30 with it.
- `CancelExecution_StopsAnEndlessPoller_AndRecordsTheReason` asserts the poller **stopped**. It
  previously checked only the recorded status, so it passed against a cancellation that recorded
  itself and stopped nothing — which is the failure the test is named for.

### Requirements

- **Dapr runtime 1.18.3 or later is now required for cancellation to be reliable.** Earlier
  runtimes silently drop a termination that is not the last event in the work item it arrives in —
  `[ExecutionTerminated, TaskCompleted]` is lost while `[ExecutionTerminated]` alone is honoured —
  leaving the workflow RUNNING forever. The event is consumed with the work item, so the terminate
  can never be re-delivered and the instance is permanently unstoppable; retrying does not help,
  and no daemon-side handling can recover it. Measured at roughly 3% of cancellations of a running
  workflow on 1.16.8 and 1.18.2, and higher under dense activity traffic. Fixed upstream in
  `dapr/durabletask-go#124`, released in Dapr **1.18.3**, and verified here: the failing
  arrangement still occurs and is now honoured every time.
  What the daemon does about it is confirm the outcome rather than trust the request —
  `TerminateAsync` waits for a terminal status — so on an older runtime a lost termination
  surfaces as a failed `CancelExecution` rather than a run recorded as cancelled while it keeps
  working.

### Breaking

- **This is a breaking change to persisted state.** Activity configs change discriminator
  (`workflow` becomes `workflow.call` / `workflow.spawn`) and promise mode changes from a
  lowercase string to an enum name, so existing `workflowexecution-{id}` streams cannot be
  replayed. `WorkflowExecutorInput` also changes shape — `LoopedTransitions` and
  `MaxLoopedTransitions` are replaced by a `LoopDetected` flag — so an orchestration in flight
  across the upgrade cannot resume either. Drop the KurrentDB volume and the SQL database before
  running this version. There is no upcaster; the daemon is pre-1.0.

  Events are also no longer stored under the CLR type's assembly-qualified name but under the name
  each one declares, so even an untouched stream would not resolve. Same instruction — drop the
  volume — and the persistent subscription group is renamed to `workflow-event-processor-v2` so a
  group left behind with the old event-type filter cannot linger and silently match nothing.

- **The document's shape rules now come from the spec, not from this repository.**
  `Common.Workflow.Validation` is deleted; `DefinitionGrpcService` applies
  `Utos.Workflow.Validation` to the bundle in its **wire form**, next to where the content digest
  is computed, because both are defined over the wire form rather than over the domain model. The
  rules are the ones in `api/docs/workflow-validation.md`, shared with the CLI and pinned by a
  conformance corpus, so a workflow the CLI accepts can no longer be rejected here for a reason of
  our own. This also picks up rules the daemon never had: a `workflows` key must equal the identity
  derived from its own metadata (`UTOS-B005`), every sub-workflow reference must resolve inside the
  bundle (`UTOS-B006`), an activity must have exactly one configuration (`UTOS-A007`), and
  `apiVersion`/`kind` must be correct (`UTOS-D001`/`UTOS-D002`).
- **Validation failures now report a code and a path**, e.g.
  `UTOS-T003 at workflows["acme/greet:1.0.0"].spec.activities["send"].onSuccess[0].transition.name`,
  and report *every* violation rather than stopping at the first. **The code is the contract; the
  message text deliberately is not**, so integration tests assert on codes and error wording stays
  free to improve without breaking anyone.
- **Activity-name references resolve ordinally.** They previously used `OrdinalIgnoreCase`, which
  disagreed with the daemon's own runtime map lookup — a workflow could validate and then fail to
  find its target at run time. Transition targets are now also checked on `onFailure` and on
  promise branches, not only on `onSuccess`.
- `StartExecutionCommandValidator` no longer re-validates the bundle. By that point the bundle
  comes from the definition store rather than from the caller, and its shape was settled when it
  was loaded.
- SDK packages moved to `0.0.11`, which renames the generated C# namespace from `Utos.Workflow.V1`
  to `Utos.Workflows.V1`. Nothing on the wire changes: the proto package is still
  `utos.workflow.v1`, so encoded bytes and content digests are identical.
- `Common.Constants.WorkflowDocument` is gone — `Utos.Workflows.V1.WorkflowDocument` ships the same
  two constants from the spec package.
- `TestWorkflowBuilder` keys bundles by canonical identity rather than by name, and resolves
  sub-workflow references to identities the way the CLI resolves a dependency alias. It had always
  produced bundles that `bundle.proto` called invalid; nothing enforced it until now.
- An event's identity on the wire is now something it declares, not a byproduct of where its class
  happens to live. The stored event type was the CLR type's `AssemblyQualifiedName` and reading one
  back was `Type.GetType` on that string, so renaming an event, moving its namespace, or moving it
  to another assembly made every event written before the move unresolvable.

  Unresolvable meant *dropped*, which is the part that mattered. `EventSerializer` caught every
  exception, logged, and returned null, and both readers took null as "skip":
  `EventStoreRepository.LoadAsync` dropped the event while still advancing the stream revision, so
  the aggregate rehydrated with a hole in its history at the correct revision and the next append
  passed its expected-version check and made the hole permanent — a lost `Created` leaves the id
  and creation time at their defaults, a lost `Failed` leaves a run reading as Active forever. The
  event processor was worse: it returned, and the caller then acked, so an unreadable event was
  acknowledged and dropped from the projection pipeline rather than parked. Neither surfaced
  anywhere but a log line. The last five refactors were safe on this only because none of them
  happened to move an event type, and nothing would have caught it if they had — `EventJsonShapeTests`
  pins each event's JSON body, but said nothing about the name it is stored under.

  Each event now declares that name with `[EventType("utos.execution.created")]`, and
  `EventTypeRegistry` resolves both directions from a startup scan of the module's assembly — an
  `IDomainEvent` without the attribute fails construction rather than being written under a name no
  reader knows. `EventTypeRegistryTests` pins the five names as literal strings, so a rename is a
  failing build instead of a lost stream.

  Deserialization throws instead of returning null. A load that throws fails the command that asked
  for it, and a subscription that throws parks the event where it can be inspected and replayed;
  both are recoverable, and neither is silent. The event processor's early return is simply
  deleted — the `Nack`/`Park` it needed was already there, one `catch` up.

  Events also carry a schema version in their KurrentDB metadata (`{"v":n}`), which was previously
  written as a literal `{}` and never read. Nothing needs an upcaster today, but a version cannot be
  added to events already written, and the seam is built and tested rather than left for the first
  migration to design under pressure: `IEventUpcaster` transforms one version to the next,
  `EventSerializer` walks the chain, and a gap in it throws. An added optional field still needs
  neither a bump nor an upcaster, which is what `AmbientEnv` and `Digest` are.

  The persistent subscription filter follows the names, from `EventTypeFilter.Prefix(
  "Execution.Domain.Events")` to `Prefix("utos.execution.")`. A group stores its filter server-side
  at creation and `CreateToAllAsync` swallows `AlreadyExists`, so a group left over from before this
  change would keep the old filter and quietly match nothing — the group name is bumped to
  `workflow-event-processor-v2` so a stale one cannot linger.

  That filter is also what keeps output entries off the projection pipeline, and the names make the
  rule say so. An `ExecutionOutputEntry` is named `utos.output.*`, outside the prefix, where it
  previously depended on the file living outside the `Execution.Domain.Events` namespace — a rule
  a routine file move could have broken with no failure, only a projection engine woken on every
  emitted value. Both halves are now asserted rather than commented.

  The JSON body of every event is unchanged, which `EventJsonShapeTests` still pins; only the name
  it is filed under moves. See **Breaking** for what that costs an existing stream.

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

- One implementation of each rule for deciding what CLR type a number is. A number's type was
  being decided at seven independent points, by four implementations, three `JsonSerializerOptions`
  instances and three deserializes that passed no options at all — and the implementations did not
  agree. The type matters: Scriban dispatches arithmetic and comparisons on the runtime type, so
  this was behaviour, not housekeeping.

  The proto edge had two converters for what is structurally the same thing — a
  `map<string, google.protobuf.Value>` — because a document arrives as a `Struct` and a run's
  input as an `ExecutionPayload`, and nobody bridged them. The document one narrowed whole numbers
  to `int`/`long`; the run-input one handed the template engine a raw `double`. Both now live in
  `ValueMapping`. The visible symptom was an id of 10^16 rendering into a URL as `1E+16` — .NET's
  shortest round-trip format for a double — so the daemon called an endpoint that did not exist.
  It had gone unnoticed because the persistence round trip narrowed the value back before any
  template saw it, and because a whole double renders identically to an int at small magnitudes.

  `NumberNarrowing` now holds both rules, and they stay deliberately **two**. Proto carries every
  number as a bare double, so the value is the only evidence there is and `5.0` becomes `int 5`.
  JSON still has the author's token, so `5.0` stays a `double`. Merging them would mean either
  giving up narrowing on the proto side or turning every whole-valued decimal an API returns into
  an integer, silently changing division on live workflows. Two fixes came with the consolidation:
  `long.MaxValue` is not representable as a double — it rounds up to 2^63 — so the old range check
  admitted 2^63 and the cast saturated back down, silently changing the number; and a non-finite
  number is now rejected at the edge, where it previously flowed inward until `System.Text.Json`
  refused to write it inside the read-model projection, parking the event and leaving a run that
  had been acknowledged to the client but was never scheduled and never listed.

  `ObjectDictionaryJsonConverter` and `StringObjectDictionaryJsonConverter` move to
  `Common.Workflow.Serialization` beside `ValueMapJsonConverter`, since they state a rule about
  workflow values rather than about infrastructure — which is also what lets
  `WorkflowExecutionCreatedEvent.Input` name one, so run input stops rehydrating from the event
  store as `JsonElement`. The read model stops deserializing without options for the same reason.
  The serialized form of every event is unchanged, which `EventJsonShapeTests` now pins with a
  populated `Input` rather than an empty one.

  Deliberately left alone: `HttpActivityHandler` still parses a response body without converters,
  so it produces `JsonElement`. That is load-bearing — a `JsonElement` re-serializes its raw token
  text, which is the only reason `10.0` from an API survives as a `double` across the Dapr hop.
  Narrowing it at parse time would collapse it to `int` one hop earlier. It survives exactly one
  hop either way; making that limit go away needs declared types, not a better guess.

  `EXECUTION_FAILED` was defined twice, in `DtoToGrpcExtensions` and `FrameToGrpcExtensions`,
  whose comment admitted the mirror; it is now `Common.Constants.WorkflowErrorCode.ExecutionFailed`.
  The frame source `"system"` was a named constant in one project and a bare literal in another,
  and the watch stream filters on string equality, so a drift would have returned nothing rather
  than failing — it is now `Observability.Domain.FrameSource.System`.
  `SubscriptionName.WorkflowEventProcessor` existed but was dead, the value hardcoded in the event
  processor; it is now used.

  No behaviour changes beyond the two bug fixes named above. The gRPC contract, the database
  schema and the event-stream format are all unchanged.

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
