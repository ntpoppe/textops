# TextOps Current Workflow

Where we're at right now.

## What Works

**Core System:**
- Users send commands via HTTP (`POST /dev/inbound`)
- Commands parsed: `run <job>`, `yes <runId>`, `no <runId>`, `status <runId>`
- Orchestrator enforces approval gating and state machine
- Everything persists to database (SQLite dev, PostgreSQL prod)

**Execution:**
- Database-backed queue (`DatabaseExecutionQueue`)
- Worker polls queue, claims work, executes jobs
- Lifecycle callbacks update run state (Running → Succeeded/Failed)

**State Machine:**
```
AwaitingApproval → (approve) → Dispatching → Running → Succeeded/Failed
                 → (deny) → Denied
```

## What's Missing

- Real messaging channels (Twilio, Telegram, etc.) - only HTTP DevApi
- Job catalog - job keys are free-form strings
- Authentication - anyone can send commands
- Scheduler - no cron/recurring jobs

## Architecture

### Projects

**TextOps.Contracts** - Shared domain types (`Run`, `RunEvent`, `InboundMessage`, `OutboundMessage`, `ParsedIntent`). No dependencies.

**TextOps.Orchestrator** - The brain. Makes all decisions.
- `DeterministicIntentParser` - Parses text commands into structured intents
- `PersistentRunOrchestrator` - Enforces state machine, approval gating, appends events, produces outbound messages

**TextOps.Channels.DevApi** - HTTP adapter for testing.
- `POST /dev/inbound` - Accept commands, call orchestrator, enqueue dispatches
- `GET /runs/{runId}` - Get run timeline
- Translates HTTP ↔ domain contracts

**TextOps.Execution** - Queue implementation.
- `DatabaseExecutionQueue` - Database-backed queue using `FOR UPDATE SKIP LOCKED` (PostgreSQL) or optimistic locking (SQLite)

**TextOps.Worker** - Standalone service that executes jobs.
- `WorkerHostedService` - Polls database queue, claims work atomically
- `IWorkerExecutor` - Executes jobs and reports lifecycle via orchestrator callbacks

**TextOps.Persistence** - Database layer.
- `TextOpsDbContext` - EF Core context (SQLite dev, PostgreSQL prod)
- `IRunRepository` - Repository for runs, events, inbox deduplication

### Flow

1. **User sends command** → DevApi receives HTTP request
2. **Parse** → `DeterministicIntentParser` extracts intent
3. **Orchestrate** → `PersistentRunOrchestrator` handles intent, enforces state machine, appends events
4. **Dispatch** → If approved, orchestrator enqueues `ExecutionDispatch` to database queue
5. **Execute** → Worker polls queue, claims work, calls `IWorkerExecutor.ExecuteAsync()`
6. **Callback** → Worker reports lifecycle (`OnExecutionStartedAsync`, `OnExecutionCompletedAsync`) back to orchestrator
7. **Complete** → Orchestrator updates run state, appends completion event, produces outbound message

### Principles

- **Orchestrator is authoritative** - All state transitions and decisions happen here
- **Channels are thin** - Translate, call orchestrator, perform side effects (send messages, enqueue)
- **Workers are stateless** - Execute jobs, report results, don't make decisions
- **Idempotency everywhere** - Duplicate messages/callbacks produce no effects
