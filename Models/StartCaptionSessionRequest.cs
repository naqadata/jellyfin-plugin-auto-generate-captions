namespace Jellyfin.Plugin.AutoGenerateCaptions.Models;

/// <summary>
/// Request body for starting an auto-generated caption session.
/// </summary>
public class StartCaptionSessionRequest
{
    /// <summary>
    /// Gets or sets the Jellyfin play session id, if the client has one.
    /// </summary>
    public string? PlaySessionId { get; set; }

    /// <summary>
    /// Gets or sets the media source id currently being played.
    /// </summary>
    public string? MediaSourceId { get; set; }

    /// <summary>
    /// Gets or sets the audio stream index currently being played.
    /// </summary>
    public int AudioStreamIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets the current playback position in ticks.
    /// </summary>
    public long PositionTicks { get; set; }

    /// <summary>
    /// Gets or sets the requested language. Empty uses plugin configuration.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the worker should continue filling the cache after playback stops.
    /// </summary>
    public bool ContinueAfterPlaybackStops { get; set; }
}
