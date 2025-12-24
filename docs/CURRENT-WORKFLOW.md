# TextOps Current Workflow (MVP)

This document describes **exactly how TextOps works today**. It reflects only what is implemented, with no future features or speculation.

---

## 1. High-Level Overview

TextOps is a human-governed job orchestration system. Users send commands via a messaging interface, and the system ensures that automated jobs require **explicit approval** before execution.

**What this MVP solves:**
- Provides a structured approval workflow for job execution
- Maintains a complete audit trail of every action
- Prevents duplicate processing through idempotency guarantees
- Enforces strict state transitions that cannot be bypassed

**What this MVP guarantees:**
- Every job execution requires human approval (no auto-execute)
- All state changes are recorded as immutable events
- State persists across restarts (SQLite for dev, PostgreSQL for prod)
- Duplicate messages produce no duplicate effects
- Invalid state transitions are rejected cleanly

**What this MVP intentionally does not do:**
- Connect to real messaging platforms (DevApi only)
- Execute real jobs (stub worker only)
- Support multiple instances (single process only)

---

## 2. Core Components and Their Roles

### TextOps.Contracts

**Purpose:** Shared type definitions used across all projects.

**Types defined here:**
- `InboundMessage` — Normalized representation of a message from any channel
- `OutboundMessage` — Message to be sent back to the user
- `Address`, `ConversationId` — Routing identifiers
- `ParsedIntent`, `IntentType` — Structured representation of user commands
- `Run`, `RunEvent`, `RunStatus` — Run state and audit trail
- `ExecutionDispatch`, `OrchestratorResult` — Execution request and response
- `IRunOrchestrator`, `IIntentParser`, `IWorkerExecutor` — Core interfaces
- `IExecutionDispatcher`, `IExecutionQueueReader` — Queue interfaces

**Why this project exists:** Contracts have no dependencies. Any project can reference them without pulling in implementation details. This enables clean architectural boundaries.

---

### TextOps.Orchestrator

**Purpose:** Core business logic. The single source of truth for run state.

#### DeterministicIntentParser

Parses raw text into structured intents using strict regex patterns:

| Command | Intent | Example |
|---------|--------|---------|
| `run <jobKey>` | `RunJob` | `run nightly-backup` |
| `yes <runId>` or `approve <runId>` | `ApproveRun` | `yes ABC123` |
| `no <runId>` or `deny <runId>` | `DenyRun` | `no ABC123` |
| `status <runId>` | `Status` | `status ABC123` |
| Anything else | `Unknown` | `hello world` |

The parser is intentionally conservative. If input doesn't match exactly, it returns `Unknown` rather than guessing.

#### PersistentRunOrchestrator

The authoritative decision-maker for all run state changes.

**Responsibilities:**
- Validates and executes state transitions
- Persists run snapshots and events to the database via `IRunRepository`
- Enforces inbound idempotency via inbox deduplication table
- Uses optimistic concurrency for safe concurrent transitions
- Produces `OutboundMessage` effects (does not send them)
- Signals when execution should be dispatched

**What the orchestrator does NOT do:**
- Send messages (returns them for the adapter to handle)
- Execute jobs (returns dispatch requests)
- Make channel-specific decisions

#### Run State Machine

```
                    ┌─────────────────────┐
                    │  AwaitingApproval   │
                    └─────────────────────┘
                           │       │
                   approve │       │ deny
                           ▼       ▼
               ┌───────────────┐ ┌─────────┐
               │  Dispatching  │ │ Denied  │ (terminal)
               └───────────────┘ └─────────┘
                       │
            execution  │
              started  │
                       ▼
               ┌───────────────┐
               │    Running    │
               └───────────────┘
                       │
            execution  │
            completed  │
                       ▼
         ┌─────────────┴─────────────┐
         │                           │
         ▼                           ▼
   ┌───────────┐              ┌───────────┐
   │ Succeeded │ (terminal)   │  Failed   │ (terminal)
   └───────────┘              └───────────┘
```

#### Event Timeline

Every state change appends one or more `RunEvent` records:

| Event Type | When Appended | Actor |
|------------|---------------|-------|
| `RunCreated` | Run is created | `user:{address}` |
| `ApprovalRequested` | After creation | `system` |
| `RunApproved` | User approves | `user:{address}` |
| `ExecutionDispatched` | After approval | `system` |
| `RunDenied` | User denies | `user:{address}` |
| `ExecutionStarted` | Worker begins | `worker:{workerId}` |
| `ExecutionSucceeded` | Worker completes successfully | `worker:{workerId}` |
| `ExecutionFailed` | Worker completes with failure | `worker:{workerId}` |

Events are append-only. They are never modified or deleted.

#### Idempotency Responsibilities

The orchestrator tracks processed messages using the key `{ChannelId}:{ProviderMessageId}`. If a message with the same key arrives twice, the second is ignored (returns empty result, no state changes, no events appended).

---

### TextOps.Channels.DevApi

**Purpose:** HTTP channel adapter for testing. Translates HTTP requests to domain contracts.

#### HTTP Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/dev/inbound` | Accept and process an inbound message |
| `GET` | `/runs/{runId}` | Retrieve run timeline |

#### What DevApi Is Allowed To Do

- Translate HTTP request → `InboundMessage`
- Call `IIntentParser.Parse()`
- Call `IRunOrchestrator.HandleInbound()`
- Enqueue `ExecutionDispatch` when orchestrator signals dispatch
- Return `OrchestratorResult` as HTTP response
- Log outbound messages to console

#### What DevApi Is NOT Allowed To Do

- Make policy decisions (approval logic, state transitions)
- Directly modify run state
- Bypass the orchestrator

#### Execution Queue and BackgroundService

DevApi hosts an in-memory execution queue (`InMemoryExecutionQueue`) using `System.Threading.Channels`. When the orchestrator returns an `ExecutionDispatch`, DevApi enqueues it.

`ExecutionHostedService` is a `BackgroundService` that:
1. Reads from the queue continuously
2. Calls `IWorkerExecutor.ExecuteAsync()` for each dispatch
3. Logs outbound messages from execution callbacks

---

### TextOps.Worker.Stub

**Purpose:** Simulates job execution for testing.

#### What "Stub" Means

This is not a real worker. It does not execute real jobs. It exists solely to:
- Demonstrate the execution lifecycle
- Enable end-to-end testing without external dependencies
- Simulate success and failure scenarios

#### What It Simulates

1. Calls `OnExecutionStarted()` immediately
2. Waits 1-2 seconds (random delay)
3. Determines outcome based on job key:
   - If `jobKey` contains "fail" → failure
   - Otherwise → success
4. Calls `OnExecutionCompleted()` with result

#### How It Interacts With The Orchestrator

```csharp
// Called by ExecutionHostedService
public async Task<OrchestratorResult> ExecuteAsync(ExecutionDispatch dispatch, CancellationToken ct)
{
    _orchestrator.OnExecutionStarted(dispatch.RunId, "worker-stub");
    
    await Task.Delay(1000-2000ms);
    
    var success = !dispatch.JobKey.Contains("fail");
    return _orchestrator.OnExecutionCompleted(dispatch.RunId, "worker-stub", success, summary);
}
```

The worker is **in-process** and calls the orchestrator directly. In a production system, workers would poll a database queue and run as separate processes.

---

## 3. End-to-End Message Flow

### 3.1 User Requests a Run

**Input:** `run demo`

```
┌──────────────────────────────────────────────────────────────────────┐
│ 1. HTTP POST /dev/inbound                                            │
│    Body: { "from": "user1", "conversation": "user1",                 │
│            "body": "run demo", "providerMessageId": "m1" }           │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 2. DevInboundController translates to InboundMessage                 │
│    ChannelId: "dev"                                                  │
│    ProviderMessageId: "m1"                                           │
│    From: Address("dev:user1")                                        │
│    Conversation: ConversationId("dev:user1")                         │
│    Body: "run demo"                                                  │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 3. DeterministicIntentParser.Parse("run demo")                       │
│    Returns: ParsedIntent(Type: RunJob, JobKey: "demo", RunId: null)  │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 4. PersistentRunOrchestrator.HandleInbound(msg, intent)              │
│    a. Check inbox: "dev:m1" not seen → proceed                       │
│    b. Mark inbox: "dev:m1" → processed                               │
│    c. Route to HandleRunJob()                                        │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 5. HandleRunJob creates run                                          │
│    RunId: "ABC123" (6-char hex)                                      │
│    JobKey: "demo"                                                    │
│    Status: AwaitingApproval                                          │
│    ChannelId: "dev"                                                  │
│    ConversationId: "dev:user1"                                       │
│    RequestedByAddress: "dev:user1"                                   │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 6. Append events                                                     │
│    RunEvent(Type: "RunCreated", Actor: "user:dev:user1")             │
│    RunEvent(Type: "ApprovalRequested", Actor: "system")              │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 7. Return OrchestratorResult                                         │
│    RunId: "ABC123"                                                   │
│    DispatchedExecution: false                                        │
│    Outbound: [OutboundMessage(                                       │
│      Body: "Job \"demo\" is ready. Reply YES ABC123 to approve..."   │
│      IdempotencyKey: "approval-request:ABC123"                       │
│    )]                                                                │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 8. DevApi returns HTTP 200 with response DTO                         │
│    Outbound message is included in response (logged for dev)         │
└──────────────────────────────────────────────────────────────────────┘
```

---

### 3.2 User Approves a Run

**Input:** `yes ABC123`

```
┌──────────────────────────────────────────────────────────────────────┐
│ 1. HTTP POST /dev/inbound                                            │
│    Body: { "body": "yes ABC123", "providerMessageId": "m2", ... }    │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 2. Parse intent                                                      │
│    Returns: ParsedIntent(Type: ApproveRun, RunId: "ABC123")          │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 3. HandleInbound routes to HandleApprove()                           │
│    a. Inbox check passes (new message ID)                            │
│    b. Load run "ABC123"                                              │
│    c. Validate status == AwaitingApproval                            │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 4. Atomic state transition                                           │
│    TryTransition(ABC123, AwaitingApproval → Dispatching)             │
│    Uses ConcurrentDictionary.TryUpdate for atomicity                 │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 5. Append events                                                     │
│    RunEvent(Type: "RunApproved", Actor: "user:dev:user1")            │
│    RunEvent(Type: "ExecutionDispatched", Actor: "system")            │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 6. Return OrchestratorResult                                         │
│    RunId: "ABC123"                                                   │
│    DispatchedExecution: true                                         │
│    Dispatch: ExecutionDispatch(RunId: "ABC123", JobKey: "demo")      │
│    Outbound: [OutboundMessage(Body: "Approved. Starting run...")]    │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 7. DevApi enqueues ExecutionDispatch                                 │
│    _executionDispatcher.Enqueue(result.Dispatch)                     │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 8. HTTP 200 returned immediately                                     │
│    Execution happens asynchronously via BackgroundService            │
└──────────────────────────────────────────────────────────────────────┘
```

---

### 3.3 Execution Lifecycle (Worker.Stub)

```
┌──────────────────────────────────────────────────────────────────────┐
│ 1. ExecutionHostedService dequeues ExecutionDispatch                 │
│    dispatch = { RunId: "ABC123", JobKey: "demo" }                    │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 2. StubWorkerExecutor.ExecuteAsync(dispatch)                         │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 3. Worker calls OnExecutionStarted("ABC123", "worker-stub")          │
│    Orchestrator:                                                     │
│    - TryTransition(Dispatching → Running)                            │
│    - Append RunEvent(Type: "ExecutionStarted")                       │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 4. Worker simulates work                                             │
│    await Task.Delay(1000-2000ms)                                     │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 5. Worker determines outcome                                         │
│    "demo" does not contain "fail" → success = true                   │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 6. Worker calls OnExecutionCompleted("ABC123", "worker-stub",        │
│                                       success: true,                 │
│                                       summary: "Job 'demo' completed │
│                                                 successfully")       │
│    Orchestrator:                                                     │
│    - TryTransition(Running → Succeeded)                              │
│    - Append RunEvent(Type: "ExecutionSucceeded")                     │
│    - Return OrchestratorResult with completion OutboundMessage       │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 7. ExecutionHostedService logs outbound message                      │
│    "OUTBOUND (dev): Run ABC123 succeeded: Job 'demo' completed..."   │
└──────────────────────────────────────────────────────────────────────┘
```

---

### 3.4 Status Query

**Input:** `status ABC123`

```
┌──────────────────────────────────────────────────────────────────────┐
│ 1. Parse intent                                                      │
│    Returns: ParsedIntent(Type: Status, RunId: "ABC123")              │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 2. HandleStatus reads run from _runs dictionary                      │
│    No state mutation occurs                                          │
│    No events are appended                                            │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│ 3. Return OrchestratorResult                                         │
│    Outbound: [OutboundMessage(                                       │
│      Body: "Run ABC123\nJob: demo\nState: Succeeded\nCreated: ..."   │
│    )]                                                                │
│    DispatchedExecution: false                                        │
└──────────────────────────────────────────────────────────────────────┘
```

Status queries are **read-only**. They consume the inbox entry (for idempotency) but do not modify run state or append events.

---

## 4. Run State Machine (Current)

### Active States

| Status | Description |
|--------|-------------|
| `AwaitingApproval` | Run created, waiting for human approval |
| `Dispatching` | Approved, execution request sent to queue |
| `Running` | Worker has started execution |

### Terminal States

| Status | Description |
|--------|-------------|
| `Succeeded` | Execution completed successfully |
| `Failed` | Execution completed with failure |
| `Denied` | Human denied the run |

### Valid Transitions

| From | To | Trigger |
|------|----|---------|
| `AwaitingApproval` | `Dispatching` | User approves (`yes <runId>`) |
| `AwaitingApproval` | `Denied` | User denies (`no <runId>`) |
| `Dispatching` | `Running` | Worker calls `OnExecutionStarted` |
| `Dispatching` | `Succeeded` | Worker calls `OnExecutionCompleted(success: true)` |
| `Dispatching` | `Failed` | Worker calls `OnExecutionCompleted(success: false)` |
| `Running` | `Succeeded` | Worker calls `OnExecutionCompleted(success: true)` |
| `Running` | `Failed` | Worker calls `OnExecutionCompleted(success: false)` |

### Invalid Transitions

Invalid transitions return an error message and do not modify state:

- Approving a run that is not `AwaitingApproval` → "Cannot approve run in state X"
- Denying a run that is not `AwaitingApproval` → "Cannot deny run in state X"
- Completing a run that is already terminal → No-op (idempotent)

Concurrent transitions are handled atomically using `ConcurrentDictionary.TryUpdate`. Only one of multiple concurrent approvals will succeed.

---

## 5. Idempotency Model (As Implemented)

### Inbound Idempotency

**Key:** `{ChannelId}:{ProviderMessageId}`

**Example:** `dev:m1`

**Behavior on duplicate:**
- Orchestrator returns `OrchestratorResult(RunId: null, Outbound: [], DispatchedExecution: false)`
- No run is created
- No events are appended
- No execution is dispatched

**Storage:** In-memory `ConcurrentDictionary<string, byte>` called `_inbox`

### Execution Idempotency

**Duplicate `OnExecutionStarted`:**
- If run is already `Running`, returns empty result (no-op)
- No duplicate `ExecutionStarted` event appended
- Uses atomic state transition check

**Duplicate `OnExecutionCompleted`:**
- If run is already terminal (`Succeeded`/`Failed`), returns empty result (no-op)
- No duplicate terminal event appended
- The first completion wins; subsequent calls have no effect

**Terminal State Protection:**
- Once a run reaches `Succeeded`, `Failed`, or `Denied`, no further state changes are possible
- This is enforced by the `TryTransition` method checking expected current state

---

## 6. Audit and Observability (Current)

### Run Timeline Contents

A `RunTimeline` contains:
- `Run` — Current snapshot (status, job key, routing info)
- `Events` — Ordered list of all `RunEvent` records

### How Events Are Appended

```csharp
private void Append(string runId, string type, DateTimeOffset at, string actor, object payload)
{
    var list = _events.GetOrAdd(runId, _ => new List<RunEvent>());
    lock (list)
    {
        list.Add(new RunEvent(runId, type, at, actor, payload));
    }
}
```

Events are appended atomically (within process). The list is protected by a lock for thread safety.

### How Timelines Are Queried

```csharp
GET /runs/{runId}
```

Returns the `Run` snapshot and a copy of all events as JSON.

### Guarantees

| Guarantee | Implementation |
|-----------|----------------|
| Append-only | Events are only added, never modified or deleted |
| Ordered | Events are appended sequentially within a run |
| Timestamped | Every event has an `At` timestamp (UTC) |
| Attributed | Every event has an `Actor` identifying who/what caused it |

---

## 7. What Happens on Restart

### Data Lost

- All runs in `_runs` dictionary
- All events in `_events` dictionary
- All inbox entries in `_inbox` dictionary
- All pending items in the execution queue

### Why This Is Acceptable For MVP

- The goal is to validate the workflow, not provide production durability
- Testing idempotency within a process lifetime is sufficient for design validation
- Persistence is a separate concern addressed in the next step

### Assumptions Currently Unsafe Across Restarts

| Assumption | Risk |
|------------|------|
| Run state persists | A restarted system has no runs |
| Duplicate detection works | Same `providerMessageId` will be processed again |
| Executions complete | In-flight executions are lost |
| Audit trail exists | Event history is gone |

---

## 8. Summary of Guarantees vs Limitations

### Guarantees Today

- **Approval enforcement** — Every run must be explicitly approved before execution
- **Deterministic parsing** — Commands are parsed with strict regex; no guessing
- **Atomic state transitions** — Concurrent operations are handled correctly
- **Idempotent inbound** — Same message twice produces no duplicate effects (within process)
- **Idempotent execution callbacks** — Duplicate started/completed calls are no-ops
- **Complete audit trail** — Every state change is recorded as an event
- **Terminal state immutability** — Completed/failed/denied runs cannot change

### Known Limitations

- **In-memory only** — All state is lost on restart
- **Single-instance only** — No shared state between processes
- **DevApi only** — No real messaging channels (Twilio, Telegram, etc.)
- **Stub execution only** — Jobs are simulated, not real
- **No persistence** — Runs, events, and inbox are ephemeral
- **No distributed queue** — Execution queue is in-process, not database-backed
- **No job catalog** — Job keys are free-form strings
- **No authentication** — Anyone can send any command
- **Outbound logging only** — Messages are logged, not actually sent

