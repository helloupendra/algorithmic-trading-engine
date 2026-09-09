namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One trader's whiteboard: an infinite canvas of notes and cards.
/// </summary>
/// <remarks>
/// The scene is stored exactly as the client saved it and the API never looks
/// inside it — the canvas editor owns that format, and every card it draws links
/// back into the console by id, so nothing here has to be queried server-side.
/// <para>
/// <see cref="Version"/> is the optimistic lock. A board is open in a browser tab
/// for hours; a second tab, or the same trader on another machine, must not
/// overwrite that work with a stale scene. Every save states the version it
/// started from and only wins when that is still the current one.
/// </para>
/// </remarks>
public class Whiteboard
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public long OwnerUserId { get; set; }

    /// <summary>The whole scene as one JSON object; a new board starts empty.</summary>
    public string SceneJson { get; set; } = "{}";

    /// <summary>Incremented by every accepted scene save; a client's compare-and-set token.</summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Who last saved — for the "someone else saved first" message.</summary>
    public string? UpdatedBy { get; set; }
}
