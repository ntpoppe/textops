# Testing with DevApi

This guide covers how to test the TextOps orchestrator using the DevApi HTTP channel adapter.

## Prerequisites

- .NET 8.0 SDK or later
- `curl` or any HTTP client

## Start the Server

```bash
dotnet run --project src/TextOps.Channels.DevApi
```

The API starts on `http://localhost:5048`. Watch the console for execution output.

---

## Basic Flow: Create → Approve → Complete

### Step 1: Create a Run

```bash
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "run demo",
    "providerMessageId": "msg-001"
  }'
```

**Response** (note the `runId`):
```json
{
  "intentType": "RunJob",
  "jobKey": "demo",
  "runId": "ABC123",
  "dispatchedExecution": false,
  "outbound": [{
    "body": "Job \"demo\" is ready. Reply YES ABC123 to approve or NO ABC123 to deny."
  }]
}
```

### Step 2: Approve the Run

Replace `ABC123` with your actual `runId`:

```bash
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "yes ABC123",
    "providerMessageId": "msg-002"
  }'
```

**Response**:
```json
{
  "intentType": "ApproveRun",
  "runId": "ABC123",
  "dispatchedExecution": true,
  "outbound": [{
    "body": "Approved. Starting run ABC123 for job \"demo\"…"
  }]
}
```

**Console output** (after 1-2 seconds):
```
OUTBOUND (dev): Run ABC123 succeeded: Job 'demo' completed successfully
```

### Step 3: View the Timeline

```bash
curl http://localhost:5048/runs/ABC123
```

---

## Simulating Failures

The stub worker fails any job where the `jobKey` contains "fail".

### Create a Failing Job

```bash
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "run deploy-fail",
    "providerMessageId": "fail-001"
  }'
```

### Approve the Failing Job

Replace the `runId` with your actual value:

```bash
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "yes RUNID",
    "providerMessageId": "fail-002"
  }'
```

**Console output**:
```
OUTBOUND (dev): Run RUNID failed: Job 'deploy-fail' failed (simulated failure)
```

### One-Liner: Fail Flow

```bash
# Create and capture runId
RUNID=$(curl -s -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{"from":"u1","conversation":"u1","body":"run test-fail","providerMessageId":"f1"}' \
  | grep -o '"runId":"[^"]*"' | cut -d'"' -f4)

echo "Created run: $RUNID"

# Approve it
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d "{\"from\":\"u1\",\"conversation\":\"u1\",\"body\":\"yes $RUNID\",\"providerMessageId\":\"f2\"}"

# Wait and check status
sleep 3
curl http://localhost:5048/runs/$RUNID | jq '.run.status'
```

---

## Denial Flow

### Create a Run

```bash
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "run risky-job",
    "providerMessageId": "deny-001"
  }'
```

### Deny the Run

```bash
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "no RUNID",
    "providerMessageId": "deny-002"
  }'
```

**Response**:
```json
{
  "intentType": "DenyRun",
  "runId": "RUNID",
  "dispatchedExecution": false,
  "outbound": [{
    "body": "Denied run RUNID for job \"risky-job\"."
  }]
}
```

---

## Status Queries

```bash
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "status RUNID",
    "providerMessageId": "status-001"
  }'
```

---

## Idempotency Demo

Sending the same `providerMessageId` twice produces no duplicate effects:

```bash
# First request - creates a run
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "run idempotent-test",
    "providerMessageId": "same-id"
  }'

# Second request - no effect (empty outbound, null runId)
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{
    "from": "user1",
    "conversation": "user1",
    "body": "run idempotent-test",
    "providerMessageId": "same-id"
  }'
```

---

## Command Reference

| Command | Example | Description |
|---------|---------|-------------|
| `run <jobKey>` | `run nightly-backup` | Create a run awaiting approval |
| `yes <runId>` | `yes ABC123` | Approve and dispatch execution |
| `approve <runId>` | `approve ABC123` | Same as `yes` |
| `no <runId>` | `no ABC123` | Deny the run |
| `deny <runId>` | `deny ABC123` | Same as `no` |
| `status <runId>` | `status ABC123` | Get current run status |

---

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/dev/inbound` | Process inbound message |
| `GET` | `/runs/{runId}` | Get run timeline (run + events) |

---

