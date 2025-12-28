using System.Text.Json;
using TextOps.Contracts.Runs;

namespace TextOps.Persistence.Entities;

/// <summary>
/// Database entity representing a run event (append-only).
/// </summary>
public sealed class RunEventEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";

    // Navigation property
    public RunEntity? Run { get; set; }

    public RunEvent ToRunEvent() => new(
        RunId: RunId,
        Type: Type,
        At: At,
        Actor: Actor,
        Payload: JsonSerializer.Deserialize<object>(PayloadJson) ?? new { }
    );

    public static RunEventEntity FromRunEvent(RunEvent runEvent) => new()
    {
        RunId = runEvent.RunId,
        Type = runEvent.Type,
        At = runEvent.At,
        Actor = runEvent.Actor,
        PayloadJson = JsonSerializer.Serialize(runEvent.Payload)
    };
}

