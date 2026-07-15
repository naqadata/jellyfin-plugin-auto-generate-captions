namespace Jellyfin.Plugin.AutoGenerateCaptions.Models;

/// <summary>
/// Request body for full-item background caption generation.
/// </summary>
public class PrefetchCaptionRequest
{
    /// <summary>
    /// Gets or sets the media source id expected for playback.
    /// </summary>
    public string? MediaSourceId { get; set; }

    /// <summary>
    /// Gets or sets the preferred audio stream index. Negative uses the first audio stream.
    /// </summary>
    public int AudioStreamIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets the requested transcription language. Empty uses server configuration.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets whether OpenAI polishing should run after transcription and diarization.
    /// Null uses server configuration.
    /// </summary>
    public bool? EnableOpenAiPolish { get; set; }

    /// <summary>
    /// Gets or sets whether a server-side Enhanced job may replace an existing sidecar subtitle.
    /// </summary>
    public bool OverwriteExistingSubtitle { get; set; }

    /// <summary>
    /// Gets or sets whether polishing should be deferred until the durable queue has drained transcription work.
    /// </summary>
    public bool DeferCaptionPolish { get; set; }
}
