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
}
