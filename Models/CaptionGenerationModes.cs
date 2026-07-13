namespace Jellyfin.Plugin.AutoGenerateCaptions.Models;

/// <summary>
/// Client-facing generated caption modes.
/// </summary>
public static class CaptionGenerationModes
{
    /// <summary>
    /// Rolling, low-latency captions generated around the playback position.
    /// </summary>
    public const string Live = "live";

    /// <summary>
    /// Full-item background transcription with optional speaker labels.
    /// </summary>
    public const string Full = "full";
}
