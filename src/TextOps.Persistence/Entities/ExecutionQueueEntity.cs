namespace TextOps.Persistence.Entities;

/// <summary>
/// Database entity for the execution queue. Workers claim and process entries.
/// Uses optimistic locking via LockedAt + LockedBy for distributed workers.
/// </summary>
public sealed class ExecutionQueueEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string JobKey { get; set; } = string.Empty;
    
    /// <summary>
    /// pending | processing | completed | failed
    /// </summary>
    public string Status { get; set; } = "pending";
    
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    
    /// <summary>
    /// Worker ID that claimed this entry. Null if unclaimed.
    /// </summary>
    public string? LockedBy { get; set; }
    
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

