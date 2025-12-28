# TextOps

TextOps is a human-governed job orchestration platform that enables users to run, approve, and monitor automated jobs via messaging channels (SMS, Telegram, Slack, etc.). The system ensures automated actions are **explicitly approved**, **auditable**, and **reliable** through an append-only event log, strict state machine transitions, and idempotency guarantees.

---

## Current State (MVP)

### What's Implemented

**Core Orchestration:**
- Deterministic command parsing (`run`, `yes`, `no`, `status`)
- Run creation with approval gating
- Strict state machine transitions (Created → AwaitingApproval → Dispatching → Running → Succeeded/Failed)
- Append-only event timeline per run
- Idempotency:
  - Inbound message deduplication via `(ChannelId, ProviderMessageId)` tuple
  - Execution callback deduplication (started/completed)

**DevApi Channel Adapter:**
- HTTP endpoint for testing without real messaging providers
- POST `/dev/inbound` - Accept inbound messages
- GET `/runs/{runId}` - Retrieve run timeline
- Automatic execution dispatch on approval
- Background service processes execution queue asynchronously

**Worker Execution:**
- Database-backed execution queue using PostgreSQL `FOR UPDATE SKIP LOCKED`
- `TextOps.Worker` polls queue, claims work, and executes jobs
- Execution lifecycle callbacks (`OnExecutionStartedAsync`, `OnExecutionCompletedAsync`)
- Completion notifications emitted to original conversation

**Persistence:**
- SQLite database for development (auto-created on startup)
- PostgreSQL support for production (via configuration)
- Runs, events, and inbox deduplication survive restarts

**Testing:**
- Comprehensive unit test suite (150+ tests)
- Integration tests for DevApi endpoints
- Persistence tests for repository and orchestrator
- Tests cover state transitions, idempotency, and error handling

### What's NOT Implemented

- **Real Messaging Channels**: Twilio SMS, Telegram, Slack adapters not yet built
- **Distributed Queue**: Using in-process queue; database-backed queue not integrated
- **Job Catalog**: Job keys are free-form strings; no schema/versioning/policies
- **Scheduler**: No cron/recurring job support
- **Authentication/Authorization**: Dev mode only; no user identity or permissions
- **Multi-Instance**: Single instance only; would require shared storage

---

## Architecture

### Projects

- **`TextOps.Contracts`**: Shared domain types (`InboundMessage`, `OutboundMessage`, `Run`, `RunEvent`, `ParsedIntent`)
- **`TextOps.Orchestrator`**: Core business logic
  - `DeterministicIntentParser`: Parses user commands into structured intents
  - `PersistentRunOrchestrator`: State machine, event log, idempotency, approval gating (database-backed)
- **`TextOps.Persistence`**: Database layer
  - `TextOpsDbContext`: EF Core context supporting SQLite and PostgreSQL
  - `EfRunRepository`: Persists runs, events, and inbox deduplication
  - Entity mappings for `Run`, `RunEvent`, `InboxEntry`, `ExecutionQueue`
- **`TextOps.Execution`**: Execution infrastructure
  - `DatabaseExecutionQueue`: Database-backed execution queue (production)
  - `InMemoryExecutionQueue`: In-memory execution queue (testing)
- **`TextOps.Channels.DevApi`**: HTTP channel adapter (translation only, no business logic)
  - Controllers translate HTTP ↔ domain contracts
  - Enqueues execution dispatches to database queue
- **`TextOps.Worker`**: Worker infrastructure
  - Polls database queue for work
  - Claims dispatches atomically
  - Executes jobs via registered `IWorkerExecutor` implementation
  - Handles retries and stale lock recovery

### Flow Diagram

```
HTTP Request (POST /dev/inbound)
  ↓
DevInboundController
  ↓
InboundMessage (contract)
  ↓
DeterministicIntentParser
  ↓
ParsedIntent
  ↓
PersistentRunOrchestrator.HandleInbound()
  ├─ Idempotency check (ChannelId:ProviderMessageId)
  ├─ State machine transition (database)
  ├─ Append RunEvent(s) (database)
  └─ Return OrchestratorResult
      ├─ OutboundMessage[] (for DevApi to log)
      └─ ExecutionDispatch? (if approved)
          ↓
          DatabaseExecutionQueue.EnqueueAsync()
          ↓
          WorkerHostedService (separate process)
          ↓
          IWorkerExecutor.ExecuteAsync()
          ├─ OnExecutionStartedAsync(runId, workerId)
          ├─ Execute job (via IWorkerExecutor implementation)
          └─ OnExecutionCompletedAsync(runId, success, summary)
              ↓
              OrchestratorResult with completion OutboundMessage
              ↓
              Worker logs: "OUTBOUND (dev): Run ABC123 succeeded: ..."
```

### Why It's Built This Way

- **Orchestrator Authority**: Only the orchestrator can change run state and append events. This prevents "channel leakage" where channel-specific logic pollutes core business rules.
- **Idempotency Keys**: Mandatory `(ChannelId, ProviderMessageId)` deduplication handles at-least-once delivery guarantees from messaging providers. Execution callbacks are also idempotent to handle worker retries.
- **Append-Only Event Timeline**: Every state change is recorded as an immutable event. Enables audit trails, replay, and debugging without corrupting history.
- **Channel Adapter Pattern**: DevApi is a thin translation layer (HTTP ↔ domain contracts). Real channels (Twilio, Telegram) will follow the same pattern, keeping core logic channel-agnostic.
- **Separation of Concerns**: Parser handles grammar, orchestrator handles state, adapters handle transport. Each component has a single responsibility.
- **DevApi for Testing**: Allows end-to-end testing without paying for Twilio or setting up webhooks. Production channels will follow the same adapter contract.

---

## Quickstart

### Prerequisites

- .NET 8.0 SDK or later
- `dotnet --version` should show 8.0.x or higher

### Run Tests

```bash
dotnet test
```

Expected output: `Passed! - Failed: 0, Passed: 108`

### Run DevApi

```bash
dotnet run --project src/TextOps.Channels.DevApi
```

The API starts on `http://localhost:5048` (HTTP) or `https://localhost:7287` (HTTPS). The console output will show the exact URL.

**Note**: The port is configured in `src/TextOps.Channels.DevApi/Properties/launchSettings.json`. To use a different port, set the `ASPNETCORE_URLS` environment variable or modify `launchSettings.json`.

### Test the System

#### 1. Create a Run

```bash
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "run demo",
    "providerMessageId": "m1"
  }'
```

**Response:**
```json
{
  "intentType": "RunJob",
  "jobKey": "demo",
  "runId": "ABC123",
  "dispatchedExecution": false,
  "outbound": [
    {
      "body": "Job \"demo\" is ready. Reply YES ABC123 to approve or NO ABC123 to deny.",
      "correlationId": "ABC123",
      "idempotencyKey": "approval-request:ABC123",
      "channelId": "dev",
      "conversation": "dev:user1"
    }
  ]
}
```

**Important**: The `providerMessageId` is used for idempotency. Sending the same `providerMessageId` twice produces no effects (empty outbound array). If omitted, DevApi auto-generates a GUID.

#### 2. Approve the Run

```bash
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "yes ABC123",
    "providerMessageId": "m2"
  }'
```

**Response:**
```json
{
  "intentType": "ApproveRun",
  "jobKey": null,
  "runId": "ABC123",
  "dispatchedExecution": true,
  "outbound": [
    {
      "body": "Approved. Starting run ABC123 for job \"demo\"…",
      "correlationId": "ABC123",
      "idempotencyKey": "approved-starting:ABC123",
      "channelId": "dev",
      "conversation": "dev:user1"
    }
  ]
}
```

**What happens**: The orchestrator transitions the run to `Dispatching`, emits an approval message, and returns an `ExecutionDispatch`. DevApi enqueues it to the database queue. A separate `TextOps.Worker` process polls the queue, claims the dispatch, and executes it via the registered `IWorkerExecutor`. The executor reports `OnExecutionStartedAsync` → `OnExecutionCompletedAsync`. Completion messages are logged: `OUTBOUND (dev): Run ABC123 succeeded: Job 'demo' completed successfully`

#### 3. Check Run Status

```bash
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "status ABC123",
    "providerMessageId": "m3"
  }'
```

#### 4. View Run Timeline

```bash
curl http://localhost:5048/runs/ABC123
```

**Response:**
```json
{
  "run": {
    "runId": "ABC123",
    "jobKey": "demo",
    "status": "Succeeded",
    "createdAt": "2024-01-01T12:00:00Z",
    "requestedByAddress": "dev:user1",
    "channelId": "dev",
    "conversationId": "dev:user1"
  },
  "events": [
    {
      "runId": "ABC123",
      "type": "RunCreated",
      "at": "2024-01-01T12:00:00Z",
      "actor": "user:dev:user1",
      "payload": { "jobKey": "demo" }
    },
    {
      "runId": "ABC123",
      "type": "ApprovalRequested",
      "at": "2024-01-01T12:00:01Z",
      "actor": "system",
      "payload": { "Policy": "DefaultRequireApproval" }
    },
    {
      "runId": "ABC123",
      "type": "RunApproved",
      "at": "2024-01-01T12:00:02Z",
      "actor": "user:dev:user1",
      "payload": {}
    },
    {
      "runId": "ABC123",
      "type": "ExecutionDispatched",
      "at": "2024-01-01T12:00:02Z",
      "actor": "system",
      "payload": {}
    },
    {
      "runId": "ABC123",
      "type": "ExecutionStarted",
      "at": "2024-01-01T12:00:03Z",
      "actor": "worker:worker-stub",
      "payload": { "WorkerId": "worker-stub" }
    },
    {
      "runId": "ABC123",
      "type": "ExecutionSucceeded",
      "at": "2024-01-01T12:00:05Z",
      "actor": "worker",
      "payload": { "Summary": "Job 'demo' completed successfully" }
    }
  ]
}
```

#### 5. Demonstrate Idempotency

Send the same message twice with the same `providerMessageId`:

```bash
# First call
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "run demo",
    "providerMessageId": "m1"
  }'

# Second call (same providerMessageId) - produces no effects
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "run demo",
    "providerMessageId": "m1"
  }'
```

**Expected**: Second call returns `"outbound": []` and `"runId": null`. No duplicate events are created.

---

## Next Steps (Roadmap)

### 1. Persistence

**Current**: All state is in-memory (`ConcurrentDictionary`). Restart loses everything.

**Plan**:
- Start with SQLite for local development
- Migrate to PostgreSQL for production
- Store: `Run` snapshots, `RunEvent` timeline, inbox deduplication keys
- Use EF Core or Dapper with append-only event table
- Consider event sourcing pattern for full replay capability

### 2. Distributed Workers (Database Queue)

**Current**: In-process `System.Threading.Channels` queue.

**Plan**:
- Add `ExecutionQueue` table to database
- Workers poll with `FOR UPDATE SKIP LOCKED` (PostgreSQL)
- Workers run as separate processes
- Stale lock recovery for crashed workers
- Retry policies with exponential backoff
- No external message broker needed (database is sufficient for this volume)

### 3. Real Channel Adapters

**Current**: Only DevApi HTTP adapter exists.

**Plan**:
- **Twilio SMS**: Webhook receiver → `InboundMessage`, SMS sender for `OutboundMessage`
- **Telegram**: Bot API → `InboundMessage`, send message for `OutboundMessage`
- **Slack**: Events API → `InboundMessage`, chat.postMessage for `OutboundMessage`
- Each adapter follows the same pattern: translate provider format ↔ domain contracts

### 4. Job Catalog + Scheduler

**Current**: Job keys are free-form strings.

**Plan**:
- **Job Catalog**: Schema definitions (parameters, versions, policies)
- **Scheduler**: Cron expressions, recurring jobs, timezone support
- **Policies**: Approval requirements per job, timeout rules, retry configs
- **Versioning**: Track job schema changes, migration paths

### 5. AI as a Subsystem

**Current**: Deterministic regex-based parsing only.

**Plan**:
- **Fallback Parser**: Use LLM when deterministic parser returns `Unknown`
- **Anomaly Detection**: Flag unusual run patterns or failures
- **Summarization**: Generate human-readable summaries of long event timelines
- **Guardrails**: Rate limiting, cost controls, fallback to deterministic parser

### 6. Authentication & Authorization

**Current**: Dev mode; no identity or permissions.

**Plan**:
- **Phone Verification**: Verify phone numbers via SMS OTP
- **Identity**: Map `Address` (opaque channel ID) → `UserId`
- **JWT/OIDC**: For web dashboard and API access
- **Per-Number Policies**: Restrict which users can approve which jobs
- **Audit**: Track who approved/denied runs

---

## Project Structure

```
src/
├── TextOps.Contracts/          # Domain types (records, enums)
│   ├── Messaging/              # InboundMessage, OutboundMessage, Address, ConversationId
│   ├── Intents/                # IntentType, ParsedIntent
│   └── Runs/                   # Run, RunEvent, RunStatus
│
├── TextOps.Orchestrator/       # Core business logic
│   ├── Parsing/                # DeterministicIntentParser
│   └── Orchestration/          # PersistentRunOrchestrator
│
├── TextOps.Persistence/        # Database layer (EF Core)
│   ├── Entities/               # RunEntity, RunEventEntity, InboxEntryEntity
│   ├── Repositories/           # EfRunRepository (implements IRunRepository from Contracts)
│   └── TextOpsDbContext.cs     # SQLite/PostgreSQL support
│
├── TextOps.Channels.DevApi/    # HTTP channel adapter
│   ├── Controllers/            # DevInboundController, RunsController
│   ├── Dtos/                   # Request/response DTOs
│   └── Program.cs              # DI registration, startup
│
├── TextOps.Execution/          # Execution infrastructure
│   ├── DatabaseExecutionQueue.cs    # Database-backed queue (production)
│   └── InMemoryExecutionQueue.cs     # In-memory queue (testing)
│
└── TextOps.Worker/             # Worker infrastructure
    ├── WorkerHostedService.cs  # Polls database queue
    └── WorkerOptions.cs        # Configuration

tests/
├── TextOps.Orchestrator.Tests/ # Unit tests (113 tests)
├── TextOps.Persistence.Tests/  # Persistence layer tests
└── TextOps.Channels.DevApi.Tests/ # Integration tests
    ├── Orchestration/          # State machine, idempotency, execution lifecycle tests
    └── Parsing/                # Parser grammar tests
```

---

## SMS Compliance

TextOps uses SMS messaging **strictly for transactional and operational purposes** related to job execution and approval workflows. No marketing, promotional, or advertising messages are sent. Users explicitly opt-in during registration and can manage preferences at any time. For questions regarding SMS usage or compliance, contact the project maintainer via this repository.
