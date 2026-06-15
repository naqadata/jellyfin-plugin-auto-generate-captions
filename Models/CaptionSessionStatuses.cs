namespace Jellyfin.Plugin.AutoGenerateCaptions.Models;

/// <summary>
/// Caption session status values.
/// </summary>
public static class CaptionSessionStatuses
{
    /// <summary>
    /// Session exists and the worker is preparing the first chunk.
    /// </summary>
    public const string WarmingUp = "warming-up";

    /// <summary>
    /// Session is generating caption chunks.
    /// </summary>
    public const string Generating = "generating";

    /// <summary>
    /// Session is serving cached captions without active generation.
    /// </summary>
    public const string Cached = "cached";

    /// <summary>
    /// Session completed generation.
    /// </summary>
    public const string Complete = "complete";

    /// <summary>
    /// Session was stopped.
    /// </summary>
    public const string Stopped = "stopped";

    /// <summary>
    /// Session failed.
    /// </summary>
    public const string Failed = "failed";
}
