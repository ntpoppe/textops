# TextOps Implementation Roadmap

This document defines the implementation roadmap for TextOps from Step 7 onward. Steps 1-6 (MVP) are complete.

---

## Why This Order

The roadmap is sequenced to build reliability before features:

1. **Persistence first (Step 7)** — Without persistence, restarts lose all state. A "governed system" that forgets approvals isn't governed.

2. **Distributed workers second (Step 8)** — Separating workers requires durable dispatch. With persistence in place, workers can poll the database reliably.

3. **Job catalog third (Step 9)** — With durability and real dispatch, you need stable job definitions. Free-form job keys work for dev, not production.

4. **Scheduler fourth (Step 10)** — "Cron that asks for approval" requires a job catalog to reference and persistence to survive restarts.

5. **Real channels fifth (Step 11)** — Channel adapters are plug-ins. The core must be reliable before adding more entry points.

6. **Observability sixth (Step 12)** — Metrics and tracing matter more once there's real traffic and distributed components.

7. **AI subsystems last (Step 13)** — AI is assistive, never authoritative. It requires a rock-solid governance foundation to safely augment.

---

## Step 7 — Persistence

### Goal

Make TextOps survive restarts. Runs, events, and idempotency state must be durable.

### Why Now

Current system is in-memory. Restart loses:
- All runs and their state
- The entire event timeline (audit trail)
- Inbox deduplication keys (enabling duplicate processing)

Without persistence, this is a demo, not a governed system.

### Components to Build

```
src/
├── TextOps.Persistence/                    # New project
│   ├── TextOps.Persistence.csproj
│   ├── TextOpsDbContext.cs                 # EF Core DbContext
│   ├── Entities/
│   │   ├── RunEntity.cs                    # Runs table
│   │   ├── RunEventEntity.cs               # Events table (append-only)
│   │   └── InboxEntryEntity.cs             # Deduplication table
│   ├── Repositories/
│   │   ├── IRunRepository.cs               # Repository interface
│   │   └── EfRunRepository.cs              # EF Core implementation
│   └── Migrations/                         # EF Core migrations
│
└── TextOps.Orchestrator/
    └── Orchestration/
        ├── IRunOrchestrator.cs             # (exists) - no change
        ├── InMemoryRunOrchestrator.cs      # (exists) - keep for tests
        └── PersistentRunOrchestrator.cs    # New - wraps repository
```

### Database Schema

#### Runs Table
```sql
CREATE TABLE Runs (
    RunId           TEXT PRIMARY KEY,
    JobKey          TEXT NOT NULL,
    Status          INTEGER NOT NULL,       -- RunStatus enum
    CreatedAt       TEXT NOT NULL,          -- ISO8601
    RequestedByAddress TEXT NOT NULL,
    ChannelId       TEXT NOT NULL,
    ConversationId  TEXT NOT NULL,
    UpdatedAt       TEXT NOT NULL,          -- For optimistic concurrency
    Version         INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX IX_Runs_Status ON Runs(Status);
CREATE INDEX IX_Runs_ChannelId_ConversationId ON Runs(ChannelId, ConversationId);
```

#### RunEvents Table (Append-Only)
```sql
CREATE TABLE RunEvents (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    RunId           TEXT NOT NULL,
    Type            TEXT NOT NULL,
    At              TEXT NOT NULL,          -- ISO8601
    Actor           TEXT NOT NULL,
    PayloadJson     TEXT NOT NULL,          -- JSON serialized
    
    FOREIGN KEY (RunId) REFERENCES Runs(RunId)
);

CREATE INDEX IX_RunEvents_RunId ON RunEvents(RunId);
-- No updates or deletes allowed (enforced in code)
```

#### InboxDedup Table
```sql
CREATE TABLE InboxDedup (
    ChannelId           TEXT NOT NULL,
    ProviderMessageId   TEXT NOT NULL,
    ProcessedAt         TEXT NOT NULL,
    RunId               TEXT,               -- Optional: which run was created
    
    PRIMARY KEY (ChannelId, ProviderMessageId)
);
```

### Interfaces

```csharp
// src/TextOps.Persistence/Repositories/IRunRepository.cs
public interface IRunRepository
{
    // Idempotency check - returns true if already processed
    Task<bool> TryMarkInboxProcessedAsync(
        string channelId, 
        string providerMessageId, 
        CancellationToken ct);
    
    // Atomic: insert run + events in one transaction
    Task CreateRunAsync(Run run, IEnumerable<RunEvent> events, CancellationToken ct);
    
    // Atomic: update run status + append events
    Task<bool> TryUpdateRunAsync(
        string runId,
        RunStatus expectedStatus,
        RunStatus newStatus,
        IEnumerable<RunEvent> events,
        CancellationToken ct);
    
    Task<Run?> GetRunAsync(string runId, CancellationToken ct);
    Task<RunTimeline?> GetTimelineAsync(string runId, CancellationToken ct);
}
```

### Idempotency Strategy

| Operation | Idempotency Key | Enforcement |
|-----------|----------------|-------------|
| Inbound message | `(ChannelId, ProviderMessageId)` | `InboxDedup` unique constraint |
| State transition | `(RunId, ExpectedStatus)` | Optimistic concurrency on `Runs.Version` |
| Event append | Implicit via transaction | Events only written with state change |

### Key Design Decisions

**SQLite vs PostgreSQL:**
- Start with **SQLite** for local dev (zero setup, file-based)
- Connection string swap to **PostgreSQL** for production
- EF Core abstracts the difference; same code works for both

**Snapshot + Events (not pure event sourcing):**
- `Runs` table stores current state (fast reads)
- `RunEvents` table stores history (audit trail)
- Updates are atomic: change snapshot + append events in one transaction
- This avoids event replay complexity while preserving audit trail

**Migration from InMemory:**
- `InMemoryRunOrchestrator` remains for unit tests
- `PersistentRunOrchestrator` implements same `IRunOrchestrator` interface
- DI registration switches implementation based on configuration

### Failure Modes to Test

| Failure | Expected Behavior |
|---------|-------------------|
| Duplicate inbound (same `ProviderMessageId`) | Returns empty result, no new run/events |
| Concurrent approvals | Only one succeeds (optimistic concurrency) |
| DB connection failure mid-transaction | Transaction rolls back, no partial state |
| Restart during execution | Run remains in `Running`; worker timeout/recovery needed |

### Acceptance Criteria

- [ ] `dotnet run` creates SQLite database on first start
- [ ] Create run → restart → `GET /runs/{runId}` returns the run
- [ ] Approve run → restart → timeline shows approval events
- [ ] Send same `providerMessageId` twice → second is no-op (even after restart)
- [ ] All existing unit tests pass (using `InMemoryRunOrchestrator`)
- [ ] New integration tests verify persistence behavior

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

### Why Now

With persistence complete:
- Orchestrator state survives crashes
- Database can reliably store dispatch queue
- Workers can poll independently

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

### Database Schema

```sql
CREATE TABLE ExecutionQueue (
    Id              SERIAL PRIMARY KEY,
    RunId           TEXT NOT NULL,
    JobKey          TEXT NOT NULL,
    Status          TEXT NOT NULL DEFAULT 'pending',  -- pending, processing, completed, failed
    CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    LockedAt        TIMESTAMPTZ,
    LockedBy        TEXT,                             -- Worker ID that claimed this
    Attempts        INTEGER NOT NULL DEFAULT 0,
    LastError       TEXT,
    CompletedAt     TIMESTAMPTZ,
    
    CONSTRAINT fk_run FOREIGN KEY (RunId) REFERENCES Runs(RunId)
);

CREATE INDEX IX_ExecutionQueue_Status ON ExecutionQueue(Status) WHERE Status = 'pending';
CREATE INDEX IX_ExecutionQueue_LockedAt ON ExecutionQueue(LockedAt) WHERE Status = 'processing';
```

### Queue Operations

#### Enqueue (Orchestrator)
```csharp
public async Task EnqueueAsync(ExecutionDispatch dispatch, CancellationToken ct)
{
    await _db.ExecutionQueue.AddAsync(new ExecutionQueueEntity
    {
        RunId = dispatch.RunId,
        JobKey = dispatch.JobKey,
        Status = "pending",
        CreatedAt = DateTimeOffset.UtcNow
    }, ct);
    await _db.SaveChangesAsync(ct);
}
```

#### Claim (Worker)
```sql
-- Atomic claim using FOR UPDATE SKIP LOCKED
UPDATE ExecutionQueue
SET Status = 'processing', 
    LockedAt = NOW(), 
    LockedBy = @workerId,
    Attempts = Attempts + 1
WHERE Id = (
    SELECT Id FROM ExecutionQueue
    WHERE Status = 'pending'
    ORDER BY CreatedAt
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
RETURNING *;
```

#### Complete (Worker)
```csharp
public async Task CompleteAsync(long queueId, bool success, string? error, CancellationToken ct)
{
    var entry = await _db.ExecutionQueue.FindAsync(queueId, ct);
    entry.Status = success ? "completed" : "failed";
    entry.CompletedAt = DateTimeOffset.UtcNow;
    entry.LastError = error;
    await _db.SaveChangesAsync(ct);
}
```

### Worker Polling

```csharp
public class WorkerHostedService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var dispatch = await _queue.ClaimNextAsync(_workerId, stoppingToken);
            
            if (dispatch == null)
            {
                // No work available, wait before polling again
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }
            
            await ProcessDispatchAsync(dispatch, stoppingToken);
        }
    }
}
```

### Idempotency Strategy

| Operation | Idempotency Key | Enforcement |
|-----------|----------------|-------------|
| Enqueue | `RunId` | Check if pending/processing entry exists |
| Claim | `FOR UPDATE SKIP LOCKED` | Database handles concurrent claims |
| Complete | `(QueueId, Status)` | Only transition from `processing` |
| Orchestrator callback | `(RunId, ExpectedStatus)` | Existing optimistic concurrency |

### Retry & Dead Letter Strategy

```csharp
// In WorkerHostedService
private async Task ProcessDispatchAsync(QueueEntry entry, CancellationToken ct)
{
    try
    {
        await _executor.ExecuteAsync(entry.Dispatch, ct);
        await _queue.CompleteAsync(entry.Id, success: true, error: null, ct);
    }
    catch (Exception ex)
    {
        if (entry.Attempts >= MaxAttempts)
        {
            // Move to failed status (dead letter equivalent)
            await _queue.CompleteAsync(entry.Id, success: false, error: ex.Message, ct);
            _logger.LogError(ex, "Dispatch failed permanently after {Attempts} attempts", entry.Attempts);
        }
        else
        {
            // Release back to pending for retry
            await _queue.ReleaseAsync(entry.Id, ex.Message, ct);
        }
    }
}
```

### Stale Lock Recovery

Workers that crash leave entries in `processing` state. A background job reclaims them:

```sql
-- Run every minute: reclaim entries locked > 5 minutes ago
UPDATE ExecutionQueue
SET Status = 'pending', LockedAt = NULL, LockedBy = NULL
WHERE Status = 'processing' 
  AND LockedAt < NOW() - INTERVAL '5 minutes';
```

### Failure Modes to Test

| Failure | Expected Behavior |
|---------|-------------------|
| Worker crashes mid-execution | Stale lock recovery reclaims after 5 min |
| Concurrent workers claim same job | `FOR UPDATE SKIP LOCKED` prevents this |
| Database unavailable | Worker retries connection with backoff |
| Max retries exceeded | Entry marked `failed`, logged for investigation |
| Duplicate enqueue (same RunId) | Rejected or no-op (check existing entry) |

### Acceptance Criteria

- [ ] Approve triggers database enqueue (visible in `ExecutionQueue` table)
- [ ] Worker (separate process) polls, claims, executes, completes
- [ ] Orchestrator receives callback, updates run to `Succeeded`/`Failed`
- [ ] Kill worker mid-execution → stale lock recovery → another worker completes
- [ ] Two workers polling → each gets different jobs (no double-processing)
- [ ] Config flag to use in-memory queue for dev (no database polling)

### Future: When to Consider a Message Broker

Add RabbitMQ/Redis Streams later if TextOps evolves to need:
- Thousands of automated jobs (bypassing human approval)
- Complex routing to different worker types
- Real-time event streaming to external systems
- Horizontal scaling beyond 10+ workers

For now, database-as-queue is simpler and sufficient

---

## Step 9 — Job Catalog

### Goal

Define jobs with schemas, versions, and policies. Runs reference catalog entries.

### Why Now

With persistence and real dispatch:
- Free-form job keys are ambiguous
- Need approval policies per job
- Need execution targets (what actually runs)

### Components to Build

```
src/
├── TextOps.Contracts/
│   └── Jobs/                               # New namespace
│       ├── JobDefinition.cs
│       ├── ApprovalPolicy.cs
│       └── ExecutionTarget.cs
│
├── TextOps.Persistence/
│   └── Entities/
│       └── JobDefinitionEntity.cs          # Jobs table
│
├── TextOps.Orchestrator/
│   └── Jobs/
│       ├── IJobCatalog.cs
│       └── PersistentJobCatalog.cs
│
└── TextOps.Channels.DevApi/
    └── Controllers/
        └── JobsController.cs               # CRUD for job definitions
```

### Data Model

```csharp
public sealed record JobDefinition(
    string JobKey,                          // Primary key
    int Version,                            // Schema version
    string DisplayName,
    string Description,
    ApprovalPolicy ApprovalPolicy,
    ExecutionTarget ExecutionTarget,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public enum ApprovalPolicy
{
    Always,         // Every run requires approval
    RiskyOnly,      // AI/rules determine if approval needed
    Never           // Auto-approve (dangerous)
}

public sealed record ExecutionTarget(
    ExecutionTargetType Type,
    string Target                           // URL, script path, MCP tool name
);

public enum ExecutionTargetType
{
    Stub,           // Use stub executor (dev/test)
    Webhook,        // POST to URL
    McpTool,        // MCP tool call (future)
    Script          // Shell script (future)
}
```

### Database Schema

```sql
CREATE TABLE JobDefinitions (
    JobKey          TEXT PRIMARY KEY,
    Version         INTEGER NOT NULL DEFAULT 1,
    DisplayName     TEXT NOT NULL,
    Description     TEXT,
    ApprovalPolicy  INTEGER NOT NULL,       -- ApprovalPolicy enum
    ExecutionTargetType INTEGER NOT NULL,
    ExecutionTarget TEXT NOT NULL,
    IsEnabled       INTEGER NOT NULL DEFAULT 1,
    CreatedAt       TEXT NOT NULL,
    UpdatedAt       TEXT NOT NULL
);
```

### Run Table Change

Add `JobVersion` to track which version of the job definition was used:

```sql
ALTER TABLE Runs ADD COLUMN JobVersion INTEGER;
```

### Idempotency Strategy

| Operation | Strategy |
|-----------|----------|
| Create job | `JobKey` unique constraint |
| Update job | Increment `Version`; old runs keep old version |
| Run creation | Capture `JobVersion` at creation time |

### Failure Modes to Test

| Failure | Expected Behavior |
|---------|-------------------|
| `run unknownJob` | Error: "Unknown job: unknownJob" |
| `run disabledJob` | Error: "Job is disabled" |
| Job updated after run created | Run uses captured version |
| Delete job with active runs | Soft delete only (set `IsEnabled = false`) |

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

### Why Now

With job catalog:
- Jobs have definitions to reference
- Approval policies determine if prompt needed
- Persistence ensures schedules survive restarts

### Components to Build

```
src/
├── TextOps.Contracts/
│   └── Scheduling/
│       ├── Schedule.cs
│       └── ScheduleTrigger.cs
│
├── TextOps.Persistence/
│   └── Entities/
│       └── ScheduleEntity.cs
│
├── TextOps.Scheduler/                      # New project
│   ├── TextOps.Scheduler.csproj
│   ├── IScheduleRepository.cs
│   ├── SchedulerHostedService.cs           # Background service
│   └── CronParser.cs                       # Parse cron expressions
│
└── TextOps.Channels.DevApi/
    └── Controllers/
        └── SchedulesController.cs          # CRUD for schedules
```

### Data Model

```csharp
public sealed record Schedule(
    string ScheduleId,
    string JobKey,
    string CronExpression,                  // "0 0 * * *" = daily at midnight
    string Timezone,                        // "America/New_York"
    bool IsEnabled,
    DateTimeOffset? NextDueAt,
    DateTimeOffset? LastTriggeredAt,
    string CreatedByAddress,
    DateTimeOffset CreatedAt
);

public sealed record ScheduleTrigger(
    string TriggerId,
    string ScheduleId,
    string JobKey,
    DateTimeOffset DueAt,
    DateTimeOffset TriggeredAt,
    string? RunId                           // Set when run is created
);
```

### Flow

```
SchedulerHostedService (every minute)
  │
  ├─ Query schedules where NextDueAt <= now
  │
  ├─ For each due schedule:
  │   ├─ Create ScheduleTrigger record
  │   ├─ Publish JobDueEvent to orchestrator
  │   └─ Update schedule.NextDueAt
  │
  └─ Orchestrator receives JobDueEvent:
      ├─ Creates run (status: AwaitingApproval or auto-approved)
      ├─ Links run to trigger
      └─ Sends approval request (if needed)
```

### Idempotency Strategy

| Operation | Idempotency Key | Enforcement |
|-----------|----------------|-------------|
| Schedule trigger | `(ScheduleId, DueAt)` | Unique constraint |
| Run from trigger | `TriggerId` | Check if trigger already has `RunId` |

### Failure Modes to Test

| Failure | Expected Behavior |
|---------|-------------------|
| Scheduler crashes | Restart picks up missed triggers (within tolerance) |
| Same trigger processed twice | Only one run created |
| Job disabled when trigger fires | Trigger recorded, no run created |
| Invalid cron expression | Rejected at schedule creation |

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

### Why Now

With persistence, distributed workers, catalog, and scheduler:
- Core is reliable
- Adding channels is low-risk
- Each channel is a thin adapter

### Implementation Order

1. **Telegram** — Free, easy webhook setup, good for testing
2. **Twilio SMS** — Production-grade, costs money, requires A2P registration
3. **Slack** — Enterprise use case, OAuth complexity

### Components to Build (Telegram Example)

```
src/
└── TextOps.Channels.Telegram/              # New project
    ├── TextOps.Channels.Telegram.csproj
    ├── Program.cs
    ├── TelegramWebhookController.cs        # Receives Telegram updates
    ├── TelegramMessageSender.cs            # Sends Telegram messages
    ├── TelegramInboundTranslator.cs        # Update → InboundMessage
    └── TelegramOutboundTranslator.cs       # OutboundMessage → SendMessage
```

### Telegram Translation

```csharp
public InboundMessage Translate(Update update)
{
    var message = update.Message!;
    
    return new InboundMessage(
        ChannelId: ChannelIds.Telegram,
        ProviderMessageId: message.MessageId.ToString(),    // Telegram's ID
        Conversation: new ConversationId($"tg:{message.Chat.Id}"),
        From: new Address($"tg:{message.From!.Id}"),
        To: null,
        Body: message.Text ?? "",
        ReceivedAt: DateTimeOffset.FromUnixTimeSeconds(message.Date),
        ProviderMeta: new Dictionary<string, string>
        {
            ["chat_type"] = message.Chat.Type.ToString(),
            ["username"] = message.From.Username ?? ""
        }
    );
}
```

### Idempotency Strategy

| Platform | Provider Message ID | Notes |
|----------|---------------------|-------|
| Telegram | `message.MessageId` | Unique per chat, not globally |
| Twilio | `MessageSid` | Globally unique |
| Slack | `event_id` | Globally unique |

**Key insight:** Telegram `MessageId` is only unique within a chat. The composite key `(ChannelId, ProviderMessageId)` handles this because `ChannelId` includes the platform.

### Failure Modes to Test

| Failure | Expected Behavior |
|---------|-------------------|
| Telegram webhook retry | Idempotency prevents duplicate runs |
| Outbound send failure | Log error, don't crash; retry later |
| Invalid message format | Return 200 (acknowledge), log warning |
| Rate limiting | Exponential backoff on sends |

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

### Components to Add

```
src/
├── TextOps.Observability/                  # New project (optional, can inline)
│   ├── Metrics/
│   │   └── TextOpsMetrics.cs               # Counter/gauge definitions
│   └── Logging/
│       └── LoggingExtensions.cs            # Structured logging helpers
│
└── All projects:
    └── Add OpenTelemetry instrumentation
```

### Metrics

| Metric | Type | Labels |
|--------|------|--------|
| `textops_runs_created_total` | Counter | `channel`, `job_key` |
| `textops_runs_completed_total` | Counter | `status` (succeeded/failed/denied) |
| `textops_approval_latency_seconds` | Histogram | `job_key` |
| `textops_execution_latency_seconds` | Histogram | `job_key`, `success` |
| `textops_queue_depth` | Gauge | `queue_name` |
| `textops_inbound_messages_total` | Counter | `channel`, `intent_type` |

### Structured Logging

Every log entry includes:
- `RunId` (when applicable)
- `ChannelId`
- `JobKey`
- `Actor`

```csharp
_logger.LogInformation(
    "Run approved. RunId={RunId} JobKey={JobKey} Actor={Actor}",
    runId, jobKey, actor);
```

### Health Checks

```csharp
// /health endpoint
builder.Services.AddHealthChecks()
    .AddDbContextCheck<TextOpsDbContext>()
    .AddCheck<ExecutionQueueHealthCheck>("execution-queue")
    .AddCheck<SchedulerHealthCheck>("scheduler");
```

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

### Subsystems

#### 1. Fallback Intent Parser

When `DeterministicIntentParser` returns `Unknown`:

```csharp
public async Task<ParsedIntent> ParseAsync(string text)
{
    // Try deterministic first
    var intent = _deterministicParser.Parse(text);
    if (intent.Type != IntentType.Unknown)
        return intent;
    
    // Fallback to AI (with confidence threshold)
    var aiIntent = await _aiParser.ParseAsync(text);
    if (aiIntent.Confidence < 0.8)
        return intent; // Return Unknown, don't guess
    
    _logger.LogInformation(
        "AI parsed intent: {IntentType} (confidence: {Confidence})",
        aiIntent.Type, aiIntent.Confidence);
    
    return aiIntent;
}
```

#### 2. Risk Scoring

Flag unusual patterns, don't auto-act:

```csharp
public record RiskAssessment(
    double Score,           // 0.0 - 1.0
    string[] Factors,       // Why it's risky
    bool RequiresExtraConfirmation
);

// Usage: High-risk runs require additional confirmation step
```

#### 3. Failure Summarization

Generate SMS-length summaries of failures:

```csharp
public async Task<string> SummarizeFailureAsync(RunTimeline timeline)
{
    // Extract failure events
    var failureEvent = timeline.Events
        .FirstOrDefault(e => e.Type == "ExecutionFailed");
    
    // AI generates human-readable summary (max 160 chars)
    return await _aiSummarizer.SummarizeAsync(failureEvent, maxLength: 160);
}
```

#### 4. Approval Explanation

Help users understand what they're approving:

```csharp
public async Task<string> ExplainApprovalAsync(Run run, JobDefinition job)
{
    // AI generates: "This will deploy v2.3.1 to production. 
    // Last deployment was 3 days ago. Reply YES {runId} to approve."
    return await _aiExplainer.ExplainAsync(run, job);
}
```

### Idempotency Strategy

AI operations are **read-only queries**. They don't need idempotency because:
- They don't change state
- Repeated calls are safe (may cost money, hence rate limits)

### Failure Modes to Test

| Failure | Expected Behavior |
|---------|-------------------|
| AI service unavailable | Fallback to deterministic, log warning |
| AI returns low confidence | Use deterministic result |
| AI latency spike | Timeout after 3s, use fallback |
| AI rate limit exceeded | Circuit breaker opens, fallback only |

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
// Already exists
IRunOrchestrator
IIntentParser
IWorkerExecutor
IExecutionDispatcher
IExecutionQueueReader

// New in Step 7
IRunRepository

// New in Step 8
IExecutionQueue              // Unified queue interface (in-memory or database)
IDatabaseExecutionQueue      // Database-specific operations (claim, release)

// New in Step 9
IJobCatalog

// New in Step 10
IScheduleRepository
ISchedulerService

// New in Step 11
IChannelAdapter         // Generic interface for all channels
IMessageSender          // Send outbound messages

// New in Step 13
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

