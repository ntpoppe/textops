# TextOps.DevApi Channel Adapter (Overview)

TextOps.DevApi provides a local HTTP API for interacting with TextOps runs—making it easy to test flows, state, and idempotency without a real messaging provider.

## Key Features

- **Simple HTTP endpoints** for testing run creation, approval/denial, and timeline retrieval
- **No business logic:** delegates everything to the orchestrator
- **Idempotency support:** use `providerMessageId` to test duplicate suppression
- **Consistent error handling:** RFC7807 ProblemDetails for all errors
- **CamelCase JSON** and auto-prefixed "dev:" addresses to match production format

## Main Endpoints

| Method | Route                | Purpose                                   |
|--------|----------------------|-------------------------------------------|
| POST   | `/dev/inbound`       | Send inbound message to orchestrator      |
| GET    | `/runs/{runId}`      | View run data & timeline events           |

**Example: Create a run**
```bash
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{"from":"user1","conversation":"user1","body":"run demo","providerMessageId":"m1"}'
```

**Example: Approve a run**
```bash
curl -X POST http://localhost:5048/dev/inbound \
  -H "Content-Type: application/json" \
  -d '{"from":"user1","conversation":"user1","body":"yes ABC123","providerMessageId":"m2"}'
```

**Example: View run timeline**
```bash
curl http://localhost:5048/runs/ABC123
```

### Error Response Example

All errors (validation, not found, etc) return RFC7807 ProblemDetails:
```json
{
  "type": "https://tools.ietf.org/html/rfc7807",
  "title": "Validation Error",
  "status": 400,
  "detail": "Field 'from' is required and cannot be empty."
}
```

## Testing & Usage

- Integration tests live in `TextOps.Channels.DevApi.Tests`
- Tests cover: happy paths, idempotency, timelines, and validation
- Start the API:  
  ```bash
  dotnet run --project src/TextOps.Channels.DevApi
  ```

## Project Structure (Short)

```
Controllers/            # API endpoints
Dtos/                   # Request/response DTOs with validation
Program.cs              # DI & startup
README.md               # (this file)
```

---

- All in-memory (no database).
- Singleton orchestrator state, but test-friendly.
- Addresses/conversations are auto-prefixed with "dev:".
- Use for rapid local/integration testing.

See the actual code and tests for full details.
