namespace Jellyfin.Plugin.AutoGenerateCaptions.Models;

/// <summary>
/// Describes one caption job for the administrative processing view.
/// </summary>
public class CaptionProcessingJobDto
{
    /// <summary>
    /// Gets or sets the caption session id.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the human-readable media title.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this is Live or Full processing.
    /// </summary>
    public string Mode { get; set; } = CaptionGenerationModes.Live;

    /// <summary>
    /// Gets or sets the worker scheduling priority.
    /// </summary>
    public string Priority { get; set; } = "live";

    /// <summary>
    /// Gets or sets the plugin session status.
    /// </summary>
    public string Status { get; set; } = CaptionSessionStatuses.WarmingUp;

    /// <summary>
    /// Gets or sets the current Full-pipeline phase when applicable.
    /// </summary>
    public string? ProcessingPhase { get; set; }

    /// <summary>
    /// Gets or sets the remote worker job id when submitted.
    /// </summary>
    public string? WorkerJobId { get; set; }

    /// <summary>
    /// Gets or sets the remote worker state when available.
    /// </summary>
    public string? WorkerState { get; set; }

    /// <summary>
    /// Gets or sets progress from zero through one hundred.
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Gets or sets the most recent processing message.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets whether speaker diarization was requested.
    /// </summary>
    public bool IsDiarized { get; set; }

    /// <summary>
    /// Gets or sets the requested language.
    /// </summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Gets or sets the session creation time.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the processing start time.
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets the terminal time for completed, stopped, cached, or failed work.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }
}
