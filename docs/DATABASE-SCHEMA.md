# TextOps Database Schema

SQL schema for TextOps persistence. Both SQLite (dev) and PostgreSQL (prod) are supported.

## Environment Configuration

| Environment | Provider   | Connection String Config Key              |
|-------------|------------|-------------------------------------------|
| Development | SQLite     | `Persistence:ConnectionStrings:Sqlite`    |
| Production  | PostgreSQL | `Persistence:ConnectionStrings:Postgres`  |

Set the provider in `appsettings.json`:

```json
{
  "Persistence": {
    "Provider": "Sqlite",  // or "Postgres"
    "ConnectionStrings": {
      "Sqlite": "Data Source=textops-dev.db",
      "Postgres": "Host=localhost;Database=textops;Username=textops;Password=secret"
    }
  }
}
```

---

## Tables

### Runs

Stores the current state snapshot of each run.

```sql
-- PostgreSQL
CREATE TABLE "Runs" (
    "RunId"              VARCHAR(50) PRIMARY KEY,
    "JobKey"             VARCHAR(200) NOT NULL,
    "Status"             INTEGER NOT NULL,
    "CreatedAt"          TIMESTAMPTZ NOT NULL,
    "RequestedByAddress" VARCHAR(500) NOT NULL,
    "ChannelId"          VARCHAR(100) NOT NULL,
    "ConversationId"     VARCHAR(500) NOT NULL,
    "UpdatedAt"          TIMESTAMPTZ NOT NULL,
    "Version"            INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX "IX_Runs_Status" ON "Runs"("Status");
CREATE INDEX "IX_Runs_ChannelId_ConversationId" ON "Runs"("ChannelId", "ConversationId");
```

```sql
-- SQLite
CREATE TABLE "Runs" (
    "RunId"              TEXT PRIMARY KEY,
    "JobKey"             TEXT NOT NULL,
    "Status"             INTEGER NOT NULL,
    "CreatedAt"          TEXT NOT NULL,
    "RequestedByAddress" TEXT NOT NULL,
    "ChannelId"          TEXT NOT NULL,
    "ConversationId"     TEXT NOT NULL,
    "UpdatedAt"          TEXT NOT NULL,
    "Version"            INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX "IX_Runs_Status" ON "Runs"("Status");
CREATE INDEX "IX_Runs_ChannelId_ConversationId" ON "Runs"("ChannelId", "ConversationId");
```

**Status Values:**

| Value | Name              |
|-------|-------------------|
| 0     | Created           |
| 1     | AwaitingApproval  |
| 2     | Approved          |
| 3     | Dispatching       |
| 4     | Running           |
| 5     | Succeeded         |
| 6     | Failed            |
| 7     | Denied            |
| 8     | Canceled          |
| 9     | TimedOut          |

---

### RunEvents (Append-Only)

Stores the complete audit timeline for each run. **Never update or delete rows.**

```sql
-- PostgreSQL
CREATE TABLE "RunEvents" (
    "Id"          SERIAL PRIMARY KEY,
    "RunId"       VARCHAR(50) NOT NULL REFERENCES "Runs"("RunId") ON DELETE CASCADE,
    "Type"        VARCHAR(100) NOT NULL,
    "At"          TIMESTAMPTZ NOT NULL,
    "Actor"       VARCHAR(500) NOT NULL,
    "PayloadJson" TEXT NOT NULL
);

CREATE INDEX "IX_RunEvents_RunId" ON "RunEvents"("RunId");
```

```sql
-- SQLite
CREATE TABLE "RunEvents" (
    "Id"          INTEGER PRIMARY KEY AUTOINCREMENT,
    "RunId"       TEXT NOT NULL,
    "Type"        TEXT NOT NULL,
    "At"          TEXT NOT NULL,
    "Actor"       TEXT NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    FOREIGN KEY ("RunId") REFERENCES "Runs"("RunId") ON DELETE CASCADE
);

CREATE INDEX "IX_RunEvents_RunId" ON "RunEvents"("RunId");
```

**Event Types:**

| Type                | Description                        |
|---------------------|------------------------------------|
| RunCreated          | Run was created                    |
| ApprovalRequested   | Approval was requested             |
| RunApproved         | Run was approved                   |
| RunDenied           | Run was denied                     |
| ExecutionDispatched | Execution was queued               |
| ExecutionStarted    | Worker started execution           |
| ExecutionSucceeded  | Execution completed successfully   |
| ExecutionFailed     | Execution failed                   |

---

### InboxDedup

Prevents duplicate processing of inbound messages.

```sql
-- PostgreSQL
CREATE TABLE "InboxDedup" (
    "ChannelId"         VARCHAR(100) NOT NULL,
    "ProviderMessageId" VARCHAR(500) NOT NULL,
    "ProcessedAt"       TIMESTAMPTZ NOT NULL,
    "RunId"             VARCHAR(50),
    PRIMARY KEY ("ChannelId", "ProviderMessageId")
);
```

```sql
-- SQLite
CREATE TABLE "InboxDedup" (
    "ChannelId"         TEXT NOT NULL,
    "ProviderMessageId" TEXT NOT NULL,
    "ProcessedAt"       TEXT NOT NULL,
    "RunId"             TEXT,
    PRIMARY KEY ("ChannelId", "ProviderMessageId")
);
```

---

## Idempotency

| Operation          | Key                                  | Enforcement                          |
|--------------------|--------------------------------------|--------------------------------------|
| Inbound message    | `(ChannelId, ProviderMessageId)`     | InboxDedup unique constraint         |
| State transition   | `(RunId, ExpectedStatus)`            | Optimistic concurrency on `Version`  |
| Event append       | Implicit                             | Events only written with state change|

---

## Notes

- EF Core manages schema creation via `EnsureCreated()` at startup
- For production PostgreSQL, consider using EF migrations for schema versioning
- SQLite stores timestamps as ISO8601 strings (TEXT), PostgreSQL uses TIMESTAMPTZ
- The `Version` column enables optimistic concurrency for state transitions

