namespace AlgoTrading.Contracts.Notebook;

/// <summary>A board as the Notebook's list shows it — everything but the scene.</summary>
public class WhiteboardSummary
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long OwnerUserId { get; set; }

    /// <summary>Filled from app_user; null only if the owner row is gone.</summary>
    public string? OwnerUserName { get; set; }

    /// <summary>The compare-and-set token a client must hand back on save.</summary>
    public int Version { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>A board with its scene, as the canvas editor opens it.</summary>
public class WhiteboardDetail : WhiteboardSummary
{
    /// <summary>The scene exactly as the client last saved it; "{}" for a new board.</summary>
    public string SceneJson { get; set; } = "{}";
}

public class CreateWhiteboardRequest
{
    /// <summary>Trimmed; empty becomes "Untitled board".</summary>
    public string? Name { get; set; }
}

public class RenameWhiteboardRequest
{
    public string? Name { get; set; }
}

public class SaveSceneRequest
{
    /// <summary>Must be one JSON object, at most 5 MB.</summary>
    public string? SceneJson { get; set; }

    /// <summary>The version the client loaded. Anything else means someone saved first.</summary>
    public int Version { get; set; }
}

public class SaveSceneResponse
{
    public int Version { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// The 409 body: what is on the server now, so the client can offer to reload
/// rather than silently losing either side's work.
/// </summary>
public class SceneConflictResponse
{
    public string Message { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
}
