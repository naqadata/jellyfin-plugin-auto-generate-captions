namespace Jellyfin.Plugin.AutoGenerateCaptions.Models;

/// <summary>
/// A durable server-side Enhanced caption job.
/// </summary>
public class CaptionQueueJobDto
{
    /// <summary>Gets or sets the durable queue job id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the Jellyfin video item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the display name captured when queued.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the queue status.</summary>
    public string Status { get; set; } = "queued";

    /// <summary>Gets or sets whether a pre-existing sidecar may be replaced.</summary>
    public bool OverwriteExistingSubtitle { get; set; }

    /// <summary>Gets or sets the active caption session id, when running.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Gets or sets the latest user-facing queue message.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the current full-item progress from zero through one hundred.</summary>
    public int ProgressPercent { get; set; }

    /// <summary>Gets or sets the current server-side processing phase.</summary>
    public string? ProcessingPhase { get; set; }

    /// <summary>Gets or sets when the job was queued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets when the job began execution.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Gets or sets when the job reached a terminal state.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}
