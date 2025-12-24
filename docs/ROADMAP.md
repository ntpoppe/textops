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

3. **Job catalog third (Step 9)** — With durability and real dispatch, you need stable job definitions. Free-form job keys work for dev, not production.

4. **Scheduler fourth (Step 10)** — "Cron that asks for approval" requires a job catalog to reference and persistence to survive restarts.

5. **Real channels fifth (Step 11)** — Channel adapters are plug-ins. The core must be reliable before adding more entry points.

6. **Observability sixth (Step 12)** — Metrics and tracing matter more once there's real traffic and distributed components.

7. **AI subsystems last (Step 13)** — AI is assistive, never authoritative. It requires a rock-solid governance foundation to safely augment.

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

## Step 9 — Job Catalog

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

## Step 10 — Scheduler Service

### Goal

"Cron that asks for approval." Scheduled jobs create runs at specified times.

### Acceptance Criteria

- [ ] Create schedule with cron expression
- [ ] Scheduler triggers job at scheduled time
- [ ] Triggered job creates run awaiting approval
- [ ] Approval timeout: run auto-denied after configurable period
- [ ] Scheduler restart: missed triggers within 5 minutes are processed
- [ ] `ApprovalPolicy.Never` jobs auto-dispatch on trigger

---

## Step 11 — Real Channels (Twilio/Telegram/Slack)

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

## Step 12 — Observability & Ops

### Goal

Production visibility: logs, metrics, traces, health checks.

### Acceptance Criteria

- [ ] `/health` returns database and queue status
- [ ] Prometheus endpoint exports metrics
- [ ] All logs include `RunId` correlation
- [ ] Grafana dashboard shows run throughput and latency
- [ ] Alerts on: high failure rate, queue backup, scheduler lag

---

## Step 13 — AI Subsystems

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
// Steps 1-7 (implemented)
IRunOrchestrator
IIntentParser
IWorkerExecutor
IExecutionDispatcher
IExecutionQueueReader
IRunRepository

// Step 8
IExecutionQueue              // Unified queue interface (in-memory or database)
IDatabaseExecutionQueue      // Database-specific operations (claim, release)

// Step 9
IJobCatalog

// Step 10
IScheduleRepository
ISchedulerService

// Step 11
IChannelAdapter         // Generic interface for all channels
IMessageSender          // Send outbound messages

// Step 13
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

Future:
- `ScheduleTriggered`
- `ApprovalTimedOut`
- `ExecutionRetried`
- `RiskFlagged`
- `AiParseUsed`
