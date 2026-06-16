using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AutoGenerateCaptions.Configuration;

/// <summary>
/// Stores Auto Generate Captions settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the absolute path to the ffmpeg binary. Empty means use Jellyfin/default PATH resolution later.
    /// </summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute path to the Python executable that can import stable_whisper.
    /// </summary>
    public string PythonPath { get; set; } = "python3";

    /// <summary>
    /// Gets or sets an optional external worker script path. Empty uses the bundled worker script.
    /// </summary>
    public string WorkerScriptPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the primary model path or model name.
    /// </summary>
    public string PrimaryModel { get; set; } = "large-v3";

    /// <summary>
    /// Gets or sets the fallback model path or model name.
    /// </summary>
    public string FallbackModel { get; set; } = "medium";

    /// <summary>
    /// Gets or sets the preferred compute backend. Use auto, cuda, vulkan, metal, or cpu.
    /// </summary>
    public string PreferredBackend { get; set; } = "cuda";

    /// <summary>
    /// Gets or sets a value indicating whether GPU startup failure should fall back to CPU.
    /// </summary>
    public bool AllowCpuFallback { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether detailed worker timing and backend logs should be emitted.
    /// </summary>
    public bool EnableVerboseWorkerLogging { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin should try an HTTP caption worker before local transcription.
    /// </summary>
    public bool EnableRemoteWorker { get; set; }

    /// <summary>
    /// Gets or sets the remote caption worker base URL.
    /// </summary>
    public string RemoteWorkerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional remote caption worker bearer token.
    /// </summary>
    public string RemoteWorkerApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model name to request from the remote caption worker. Empty uses PrimaryModel.
    /// </summary>
    public string RemoteWorkerModel { get; set; } = "large-v3";

    /// <summary>
    /// Gets or sets a value indicating whether local transcription should be used when the remote worker is unavailable before a job starts.
    /// </summary>
    public bool RemoteWorkerFallbackToLocal { get; set; } = true;

    /// <summary>
    /// Gets or sets the remote worker health check timeout in seconds.
    /// </summary>
    public int RemoteWorkerHealthTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets the remote worker job timeout in seconds.
    /// </summary>
    public int RemoteWorkerJobTimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// Gets or sets the remote worker polling interval in seconds.
    /// </summary>
    public int RemoteWorkerPollSeconds { get; set; } = 2;

    /// <summary>
    /// Gets or sets the default transcription language. Use auto to let the worker detect it.
    /// </summary>
    public string DefaultLanguage { get; set; } = "auto";

    /// <summary>
    /// Gets or sets the first audio chunk size in seconds.
    /// </summary>
    public int InitialChunkSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets the steady-state audio chunk size in seconds.
    /// </summary>
    public int ChunkSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the target generated-caption lookahead in seconds.
    /// </summary>
    public int LookaheadSeconds { get; set; } = 90;

    /// <summary>
    /// Gets or sets the Roku/client polling interval in seconds.
    /// </summary>
    public int PollSeconds { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum number of active transcription sessions.
    /// </summary>
    public int MaxConcurrentSessions { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether partial generated captions should be cached.
    /// </summary>
    public bool CachePartialResults { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether completed subtitles should be saved next to media.
    /// </summary>
    public bool PromoteCompletedSubtitles { get; set; }

    /// <summary>
    /// Gets or sets comma-separated model names that are considered good enough for stitched cache output and future external subtitle promotion.
    /// Empty means any model is allowed.
    /// </summary>
    public string PromotableModels { get; set; } = "large-v3, large-v3-turbo";

    /// <summary>
    /// Gets or sets an optional cache directory. Empty means a plugin data folder will be used later.
    /// </summary>
    public string CacheDirectory { get; set; } = string.Empty;
}
