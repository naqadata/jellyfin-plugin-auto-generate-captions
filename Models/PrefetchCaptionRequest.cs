namespace Jellyfin.Plugin.AutoGenerateCaptions.Models;

/// <summary>
/// Request body for speculative full-item caption generation.
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
}
