# Roadmap — Human-Governed Job Orchestration Platform (MVP → v1)

This roadmap is structured to maximize progress **while Twilio verification is pending**, avoid rework, and keep the system architecturally sound.

The ordering is intentional. Do not skip steps.

---

## Phase 0 — Foundations (Day 0–1)

**Goal:** Establish non-negotiable contracts and boundaries.

### Deliverables
- Define provider-agnostic message models:
  - `InboundMessage`
  - `OutboundMessage`
- Define `ParsedIntent` contract
- Define Run state machine states (enum only)
- Decide service boundaries (even if still in one process)

### Exit Criteria
- No Twilio-specific types outside adapters
- Core logic does not reference phone numbers or chat IDs directly

---

## Phase 1 — Fake Messaging Gateway (Day 1–2)

**Goal:** Replace SMS with a deterministic, testable input channel.

### Deliverables
- `POST /dev/messages/inbound`
- Logs outbound messages instead of sending
- Dedupe support via `messageId`
- Emits `InboundMessageReceived` event

### Exit Criteria
- You can inject messages manually
- System behaves identically regardless of channel

---

## Phase 2 — Command Parsing (Rules-Only) (Day 2–3)

**Goal:** Convert text into structured intent without AI.

### Deliverables
- Deterministic parser:
  - `run <job>`
  - `yes <runId>` / `approve <runId>`
  - `status <runId>`
- Produces `ParsedIntent`
- Rejects ambiguous input cleanly

### Exit Criteria
- No string parsing inside orchestrator
- Parser is replaceable

---

## Phase 3 — Run Orchestrator (In-Memory) (Day 3–5)

**Goal:** Implement the system’s brain before persistence.

### Deliverables
- In-memory Run store
- Append-only Run event log
- State transitions enforced by code
- Approval timeout logic (simple)

### Run States
