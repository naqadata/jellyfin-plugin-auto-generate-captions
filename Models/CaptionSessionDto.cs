namespace Jellyfin.Plugin.AutoGenerateCaptions.Models;

/// <summary>
/// Describes an auto-generated caption session.
/// </summary>
public class CaptionSessionDto
{
    /// <summary>
    /// Gets or sets the session id.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the media source id.
    /// </summary>
    public string? MediaSourceId { get; set; }

    /// <summary>
    /// Gets or sets the audio stream index.
    /// </summary>
    public int AudioStreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the requested language.
    /// </summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Gets or sets the current session status.
    /// </summary>
    public string Status { get; set; } = CaptionSessionStatuses.WarmingUp;

    /// <summary>
    /// Gets or sets the relative URL for the live WebVTT stream.
    /// </summary>
    public string LiveVttUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the requested client poll interval in seconds.
    /// </summary>
    public int PollSeconds { get; set; }

    /// <summary>
    /// Gets or sets the last generated position in ticks.
    /// </summary>
    public long GeneratedThroughTicks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether cached captions were available for this session.
    /// </summary>
    public bool HasCachedCaptions { get; set; }

    /// <summary>
    /// Gets or sets whether this is a rolling live session or a full-item session.
    /// </summary>
    public string Mode { get; set; } = CaptionGenerationModes.Live;

    /// <summary>
    /// Gets or sets full-item processing progress from zero through one hundred.
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Gets or sets the current machine-readable processing phase for Full captions.
    /// </summary>
    public string? ProcessingPhase { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether speaker diarization is enabled for this session.
    /// </summary>
    public bool IsDiarized { get; set; }

    /// <summary>
    /// Gets or sets the media duration in ticks when known.
    /// </summary>
    public long? DurationTicks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a durable Enhanced subtitle is ready.
    /// </summary>
    public bool EnhancedReady { get; set; }

    /// <summary>
    /// Gets or sets the user-facing title of the promoted Enhanced subtitle.
    /// </summary>
    public string? EnhancedDisplayTitle { get; set; }

    /// <summary>
    /// Gets or sets the relative URL used to switch to the completed Enhanced VTT.
    /// </summary>
    public string? EnhancedVttUrl { get; set; }
}
