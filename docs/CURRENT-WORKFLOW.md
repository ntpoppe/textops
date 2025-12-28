# TextOps Current Workflow

A concise overview of how TextOps works today.

---

## What TextOps Does

TextOps is a human-governed job orchestration system. Users send commands via messaging, and the system ensures automated jobs require **explicit approval** before execution.

**Key Guarantees:**
- Every job execution requires human approval
- Complete audit trail (append-only events)
- Idempotent operations (duplicates produce no effects)
- State persists across restarts (SQLite/PostgreSQL)

---

## Components

### TextOps.Contracts
Shared types and interfaces. No dependencies. Defines the domain language.

### TextOps.Orchestrator
**The brain.** Makes all decisions about run state.

- **DeterministicIntentParser**: Parses text commands into structured intents
- **PersistentRunOrchestrator**: Enforces state machine, persists to database, produces outbound messages

### TextOps.Channels.DevApi
HTTP adapter for testing. Translates HTTP ↔ domain contracts.

- `POST /dev/inbound` - Accept commands
- `GET /runs/{runId}` - Get run timeline

### TextOps.Worker
Real worker infrastructure that polls the database queue, claims work, and executes jobs.

### TextOps.Persistence
Database layer (SQLite dev, PostgreSQL prod). Stores runs, events, inbox deduplication, execution queue.

---

## Command Flow

### 1. User Requests a Run

```
User → DevApi → Parser → Orchestrator
                      ↓
              Create Run (AwaitingApproval)
                      ↓
              Return approval request message
```

**Example:** `run demo`

- Parser extracts `JobKey: "demo"`
- Orchestrator creates run with status `AwaitingApproval`
- Appends `RunCreated` and `ApprovalRequested` events
- Returns message: "Job 'demo' is ready. Reply YES {runId} to approve..."

### 2. User Approves

```
User → DevApi → Parser → Orchestrator
                      ↓
              Transition: AwaitingApproval → Dispatching
                      ↓
              Append RunApproved + ExecutionDispatched events
                      ↓
              Return ExecutionDispatch
                      ↓
              DevApi enqueues dispatch
```

**Example:** `yes ABC123`

- Orchestrator validates run is `AwaitingApproval`
- Atomically transitions to `Dispatching`
- Appends events
- Returns `ExecutionDispatch` for queue

### 3. Execution Lifecycle

```
Database Queue → WorkerHostedService → IWorkerExecutor
                                              ↓
                                    OnExecutionStartedAsync()
                                              ↓
                                    Transition: Dispatching → Running
                                              ↓
                                    Execute job (via IWorkerExecutor)
                                              ↓
                                    OnExecutionCompletedAsync()
                                              ↓
                                    Transition: Running → Succeeded/Failed
```

**Worker Infrastructure:**
- Polls database queue for pending dispatches
- Claims work atomically using `FOR UPDATE SKIP LOCKED`
- Calls registered `IWorkerExecutor.ExecuteAsync()` for each dispatch
- Handles retries, stale lock recovery, and error handling
- Reports lifecycle via orchestrator callbacks

---

## State Machine

```
AwaitingApproval
    ├─ approve → Dispatching
    └─ deny → Denied (terminal)

Dispatching
    └─ execution started → Running

Running
    └─ execution completed → Succeeded | Failed (terminal)
```

**Terminal States:** `Succeeded`, `Failed`, `Denied` (immutable)

**Invalid Transitions:** Rejected with error message, no state change

---

## Commands

| Command | Intent | Example |
|---------|--------|---------|
| `run <jobKey>` | RunJob | `run nightly-backup` |
| `yes <runId>` or `approve <runId>` | ApproveRun | `yes ABC123` |
| `no <runId>` or `deny <runId>` | DenyRun | `no ABC123` |
| `status <runId>` | Status | `status ABC123` |
| Anything else | Unknown | Returns error message |

---

## Idempotency

### Inbound Messages
**Key:** `{ChannelId}:{ProviderMessageId}` (e.g., `dev:m1`)

Duplicate messages return empty result, no state changes, no events.

### Execution Callbacks
- Duplicate `OnExecutionStarted`: No-op if already `Running`
- Duplicate `OnExecutionCompleted`: No-op if already terminal

---

## Event Timeline

Every state change appends immutable `RunEvent` records:

| Event | When | Actor |
|-------|------|-------|
| `RunCreated` | Run created | `user:{address}` |
| `ApprovalRequested` | After creation | `system` |
| `RunApproved` | User approves | `user:{address}` |
| `ExecutionDispatched` | After approval | `system` |
| `ExecutionStarted` | Worker begins | `worker:{workerId}` |
| `ExecutionSucceeded` | Worker succeeds | `worker:{workerId}` |
| `ExecutionFailed` | Worker fails | `worker:{workerId}` |
| `RunDenied` | User denies | `user:{address}` |

Events are **append-only** and **ordered**. Query via `GET /runs/{runId}`.

---

## Architecture Principles

**Orchestrator is authoritative:**
- All state transitions happen here
- All events are appended here
- All policy decisions made here

**Adapters are thin:**
- Translate channel ↔ domain contracts
- Call orchestrator
- Perform side effects (send messages, enqueue dispatches)

**Workers are stateless:**
- Execute jobs
- Report lifecycle via orchestrator callbacks
- Do not make policy decisions

---

## Current Limitations

- **DevApi only** - No real messaging channels (Twilio, Telegram, etc.)
- **No job catalog** - Job keys are free-form strings
- **No authentication** - Anyone can send commands
