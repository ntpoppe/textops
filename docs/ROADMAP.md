# TextOps Implementation Roadmap

This document defines the implementation roadmap for TextOps. Steps 1-7 are complete.

---

## Completed Steps

- **Step 1-6 (MVP)** — Core orchestration, parser, run lifecycle, DevApi channel
- **Step 7 (Persistence)** — SQLite/PostgreSQL with EF Core, `PersistentRunOrchestrator`, inbox deduplication. See `docs/DATABASE-SCHEMA.md` for schema details.

---

## Why This Order

The roadmap is sequenced to build reliability before features:

1. ~~**Persistence first (Step 7)** — Done. System now survives restarts.~~

2. **Distributed workers next (Step 8)** — Separating workers requires durable dispatch. With persistence in place, workers can poll the database reliably.

3. **Run lifecycle third (Step 9)** — Stale runs accumulate. Expiration, reminders, and cleanup prevent unbounded growth before adding more features.

4. **Job catalog fourth (Step 10)** — With durability and lifecycle management, you need stable job definitions. Free-form job keys work for dev, not production.

5. **Scheduler fifth (Step 11)** — "Cron that asks for approval" requires a job catalog to reference and persistence to survive restarts.

6. **Real channels sixth (Step 12)** — Channel adapters are plug-ins. The core must be reliable before adding more entry points.

7. **Observability seventh (Step 13)** — Metrics and tracing matter more once there's real traffic and distributed components.

8. **AI subsystems last (Step 14)** — AI is assistive, never authoritative. It requires a rock-solid governance foundation to safely augment.

---

## Step 8 — Distributed Workers (Database Queue)

### Goal

Enable workers to run as separate processes using the database as a reliable queue.

### Why Database-as-Queue (Not RabbitMQ)

TextOps is a human-governed approval system. The volume is naturally low (humans are the bottleneck). A message broker like RabbitMQ adds operational complexity for capabilities we don't need:

| RabbitMQ Excels At | TextOps Needs |
|--------------------|---------------|
| 100k+ messages/sec | Tens to hundreds/day |
| Complex routing (topics, fanout) | Simple point-to-point |
| Multiple consumer types | 1-2 worker instances |
| Stream processing | Simple job dispatch |

**Database-as-queue benefits:**
- No additional infrastructure (already have PostgreSQL)
- ACID guarantees on dispatch
- Simple to implement and debug
- Battle-tested pattern (Sidekiq, Delayed Job, etc.)

### Components to Build

```
src/
├── TextOps.Persistence/
│   └── Entities/
│       └── ExecutionQueueEntity.cs         # Queue table
│
├── TextOps.Execution/
│   ├── InMemoryExecutionQueue.cs           # (exists) - keep for in-proc dev
│   ├── IExecutionQueue.cs                  # Unified interface
│   └── DatabaseExecutionQueue.cs           # New - database-backed queue
│
└── TextOps.Worker/                         # New project (standalone service)
    ├── TextOps.Worker.csproj
    ├── Program.cs
    ├── WorkerHostedService.cs              # Polls database queue
    └── appsettings.json
```

### Acceptance Criteria

- [ ] Approve triggers database enqueue (visible in `ExecutionQueue` table)
- [ ] Worker (separate process) polls, claims, executes, completes
- [ ] Orchestrator receives callback, updates run to `Succeeded`/`Failed`
- [ ] Kill worker mid-execution → stale lock recovery → another worker completes
- [ ] Two workers polling → each gets different jobs (no double-processing)
- [ ] Config flag to use in-memory queue for dev (no database polling)

---

## Step 9 — Run Lifecycle Management

### Goal

Runs should not live forever. Add expiration, reminders, cleanup, and ID collision handling.

### Why Now

With persistence in place, stale runs accumulate indefinitely. Before adding more features:
- Unapproved runs should expire
- Users need reminders for pending approvals
- The 6-char run ID (~16.7M combinations) needs collision handling
- Old data needs retention policies

### Components

```
src/
├── TextOps.Contracts/
│   └── Runs/
│       └── RunStatus.cs                    # Add: Expired (new terminal state)
│
├── TextOps.Orchestrator/
│   └── Orchestration/
│       └── PersistentRunOrchestrator.cs    # Handle expiration transitions
│
├── TextOps.Persistence/
│   ├── Repositories/
│   │   └── EfRunRepository.cs              # Add: collision detection, bulk expiration
│   └── Services/
│       └── RunLifecycleService.cs          # Background service for expiration/reminders
│
└── TextOps.Channels.DevApi/
    └── appsettings.json                    # Lifecycle configuration
```

### Configuration

```json
{
  "RunLifecycle": {
    "ApprovalTimeoutMinutes": 1440,         // 24 hours default
    "ReminderIntervalMinutes": 60,          // Remind every hour
    "MaxReminders": 3,                      // Stop after 3 reminders
    "RetentionDays": 90,                    // Keep completed runs for 90 days
    "RunIdLength": 6,                       // Current: 6 hex chars
    "RunIdRetryOnCollision": true           // Regenerate on collision
  }
}
```

### State Machine Update

```
                    ┌─────────────────────┐
                    │  AwaitingApproval   │
                    └─────────────────────┘
                       │       │       │
               approve │       │ deny  │ timeout
                       ▼       ▼       ▼
           ┌───────────────┐ ┌─────────┐ ┌─────────┐
           │  Dispatching  │ │ Denied  │ │ Expired │ (NEW)
           └───────────────┘ └─────────┘ └─────────┘
```

### Acceptance Criteria

**Expiration:**
- [ ] New `RunStatus.Expired` terminal state
- [ ] Runs in `AwaitingApproval` auto-expire after configurable timeout
- [ ] `RunExpired` event appended on expiration (actor: `system:lifecycle`)
- [ ] Expiration message sent to originating conversation
- [ ] Expired runs cannot be approved/denied (proper error message)

**Reminders:**
- [ ] Reminder message sent at configurable intervals
- [ ] `ApprovalReminder` event appended for each reminder
- [ ] Max reminder count configurable (stop spamming)
- [ ] Reminder includes time remaining before expiration

**Run ID Collision Handling:**
- [ ] `CreateRunAsync` detects ID collision
- [ ] Auto-retry with new ID (configurable max attempts)
- [ ] If all retries fail, return error (don't silently overwrite)
- [ ] Metric: collision rate (early warning for ID exhaustion)

**Data Retention:**
- [ ] Completed runs (Succeeded/Failed/Denied/Expired) older than retention period are soft-deleted
- [ ] Soft-delete: `IsDeleted` flag, excluded from queries
- [ ] Hard-delete: separate cleanup job (optional, for GDPR compliance)
- [ ] Inbox entries cleaned up with their associated runs

**Tests:**
- [ ] Expiration transitions work correctly
- [ ] Reminders are sent at correct intervals
- [ ] Collision detection and retry work
- [ ] Retention cleanup respects retention period
- [ ] Terminal state runs cannot be expired (idempotency)

---

## Step 10 — Job Catalog

### Goal

Define jobs with schemas, versions, and policies. Runs reference catalog entries.

### Why Now

With persistence and real dispatch:
- Free-form job keys are ambiguous
- Need approval policies per job
- Need execution targets (what actually runs)

### Acceptance Criteria

- [ ] `POST /jobs` creates a job definition
- [ ] `GET /jobs` lists all jobs
- [ ] `run existingJob` creates run with `JobVersion`
- [ ] `run unknownJob` returns error (unless dev mode)
- [ ] Job update increments version; existing runs unaffected
- [ ] `ApprovalPolicy.Never` auto-approves (with warning log)

---

## Step 11 — Scheduler Service

### Goal

"Cron that asks for approval." Scheduled jobs create runs at specified times.

### Acceptance Criteria

- [ ] Create schedule with cron expression
- [ ] Scheduler triggers job at scheduled time
- [ ] Triggered job creates run awaiting approval
- [ ] Scheduler restart: missed triggers within 5 minutes are processed
- [ ] `ApprovalPolicy.Never` jobs auto-dispatch on trigger

---

## Step 12 — Real Channels (Twilio/Telegram/Slack)

### Goal

Accept messages from real messaging platforms. Same orchestrator, different entry points.

### Implementation Order

1. **Telegram** — Free, easy webhook setup, good for testing
2. **Twilio SMS** — Production-grade, costs money, requires A2P registration
3. **Slack** — Enterprise use case, OAuth complexity

### Acceptance Criteria

- [ ] Send Telegram message → run created
- [ ] Approve via Telegram → execution dispatched
- [ ] Completion message sent to Telegram chat
- [ ] Same message delivered twice → only one run
- [ ] Bot restart → no duplicate processing

---

## Step 13 — Observability & Ops

### Goal

Production visibility: logs, metrics, traces, health checks.

### Acceptance Criteria

- [ ] `/health` returns database and queue status
- [ ] Prometheus endpoint exports metrics
- [ ] All logs include `RunId` correlation
- [ ] Grafana dashboard shows run throughput and latency
- [ ] Alerts on: high failure rate, queue backup, scheduler lag

---

## Step 14 — AI Subsystems

### Goal

AI assists humans. It never bypasses governance.

### Hard Constraints

1. **AI cannot approve runs** — Only humans approve
2. **AI cannot dispatch execution** — Only orchestrator dispatches
3. **AI cannot mutate state** — Read-only access to runs/events
4. **AI has rate limits** — Fallback to deterministic behavior
5. **AI is optional** — System works without AI

### Acceptance Criteria

- [ ] "please run the backup job" → AI parses as `run backup`
- [ ] Low confidence AI result → returns `Unknown` intent
- [ ] AI unavailable → system works normally (deterministic only)
- [ ] Risk score > 0.7 → approval message includes warning
- [ ] Failure summary fits in SMS (≤160 chars)
- [ ] `/ai/status` shows AI subsystem health and rate limit status

---

## Appendix: Interface Summary

### Core Interfaces (Contracts)

```csharp
// Steps 1-8 (implemented)
IRunOrchestrator
IIntentParser
IWorkerExecutor
IExecutionDispatcher         // Simple enqueue interface
IExecutionQueue              // Full queue interface (claim, complete, release, reclaim)
IRunRepository

// Step 9
IRunLifecycleService         // Expiration, reminders, cleanup

// Step 10
IJobCatalog

// Step 11
IScheduleRepository
ISchedulerService

// Step 12
IChannelAdapter         // Generic interface for all channels
IMessageSender          // Send outbound messages

// Step 14
IAiIntentParser
IAiRiskScorer
IAiSummarizer
```

### Event Types (RunEvent.Type)

Current:
- `RunCreated`
- `ApprovalRequested`
- `RunApproved`
- `RunDenied`
- `ExecutionDispatched`
- `ExecutionStarted`
- `ExecutionSucceeded`
- `ExecutionFailed`

Future (Step 9 - Lifecycle):
- `ApprovalReminder`
- `RunExpired`

Future (Step 11 - Scheduler):
- `ScheduleTriggered`

Future (Step 14 - AI):
- `RiskFlagged`
- `AiParseUsed`
