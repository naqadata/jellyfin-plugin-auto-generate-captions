using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.AutoGenerateCaptions.Configuration;
using Jellyfin.Plugin.AutoGenerateCaptions.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoGenerateCaptions.Services;

/// <summary>
/// Coordinates live auto-generated caption sessions.
/// </summary>
public class AutoGenerateCaptionService
{
    private const long TicksPerSecond = 10_000_000;
    private readonly ConcurrentDictionary<Guid, CaptionSessionState> _sessions = new();
    private readonly ILogger<AutoGenerateCaptionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoGenerateCaptionService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public AutoGenerateCaptionService(ILogger<AutoGenerateCaptionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts a caption generation session.
    /// </summary>
    /// <param name="video">The video item.</param>
    /// <param name="request">The start request.</param>
    /// <returns>The new session.</returns>
    public CaptionSessionDto StartSession(Video video, StartCaptionSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(request);

        PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var sessionId = Guid.NewGuid();
        string language = string.IsNullOrWhiteSpace(request.Language)
            ? config.DefaultLanguage
            : request.Language.Trim();

        var state = new CaptionSessionState
        {
            SessionId = sessionId,
            ItemId = video.Id,
            ItemName = GetDisplayName(video),
            MediaPath = video.Path,
            MediaSourceId = request.MediaSourceId,
            AudioStreamIndex = request.AudioStreamIndex,
            Language = language,
            Status = CaptionSessionStatuses.WarmingUp,
            PollSeconds = Math.Clamp(config.PollSeconds, 1, 30),
            StartPositionTicks = Math.Max(0, request.PositionTicks),
            GeneratedThroughTicks = Math.Max(0, request.PositionTicks),
            Message = "Session created. Worker implementation is not wired yet."
        };

        _sessions[sessionId] = state;
        _logger.LogInformation(
            "Created auto-caption session {SessionId} for {ItemName} at {PositionTicks} ticks; mediaSourceId={MediaSourceId}; audioStreamIndex={AudioStreamIndex}; language={Language}",
            sessionId,
            state.ItemName,
            state.StartPositionTicks,
            state.MediaSourceId,
            state.AudioStreamIndex,
            state.Language);

        _logger.LogInformation(
            "Auto-caption worker policy for session {SessionId}: primaryModel={PrimaryModel}; fallbackModel={FallbackModel}; preferredBackend={PreferredBackend}; allowCpuFallback={AllowCpuFallback}; initialChunkSeconds={InitialChunkSeconds}; chunkSeconds={ChunkSeconds}; lookaheadSeconds={LookaheadSeconds}; cachePartialResults={CachePartialResults}; promoteCompletedSubtitles={PromoteCompletedSubtitles}",
            sessionId,
            config.PrimaryModel,
            config.FallbackModel,
            config.PreferredBackend,
            config.AllowCpuFallback,
            config.InitialChunkSeconds,
            config.ChunkSeconds,
            config.LookaheadSeconds,
            config.CachePartialResults,
            config.PromoteCompletedSubtitles);

        if (config.EnableVerboseWorkerLogging)
        {
            _logger.LogInformation(
                "Auto-caption verbose worker logging enabled for session {SessionId}; future worker logs should include backend probe results, GPU device name/VRAM when available, model load duration, warmup duration, ffmpeg startup duration, chunk inference duration, realtime factor, and fallback reason",
                sessionId);
        }

        // TODO: Attach the ffmpeg/Whisper worker here. It should update Ranges,
        // GeneratedThroughTicks, Cues, Status, and Message as chunks complete.
        // Required worker log points:
        // - backend probe start/result: requested backend, selected backend, GPU name, VRAM, driver/runtime version when available
        // - model selection: primary/fallback, model path, quantization, language
        // - model load/warm timings and whether the model stayed resident
        // - ffmpeg command, seek position, stream index, startup/extraction timings
        // - per-chunk inference timings, realtime factor, generated range, cache write path
        // - fallback decisions and CPU/GPU transitions
        return ToDto(state);
    }

    /// <summary>
    /// Gets session status.
    /// </summary>
    /// <param name="sessionId">Session id.</param>
    /// <returns>Status, or null when not found.</returns>
    public CaptionSessionStatusDto? GetStatus(Guid sessionId)
    {
        return _sessions.TryGetValue(sessionId, out CaptionSessionState? state) ? ToStatusDto(state) : null;
    }

    /// <summary>
    /// Stops a session.
    /// </summary>
    /// <param name="sessionId">Session id.</param>
    /// <returns>Stopped session status, or null when not found.</returns>
    public CaptionSessionStatusDto? StopSession(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out CaptionSessionState? state))
        {
            return null;
        }

        state.Status = CaptionSessionStatuses.Stopped;
        state.Message = "Session stopped by client.";
        state.StoppedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("Stopped auto-caption session {SessionId}", sessionId);
        return ToStatusDto(state);
    }

    /// <summary>
    /// Builds the current live WebVTT payload for a session.
    /// </summary>
    /// <param name="sessionId">Session id.</param>
    /// <param name="positionTicks">Current playback position in ticks.</param>
    /// <returns>WebVTT text, or null when not found.</returns>
    public string? GetLiveVtt(Guid sessionId, long? positionTicks)
    {
        if (!_sessions.TryGetValue(sessionId, out CaptionSessionState? state))
        {
            return null;
        }

        if (positionTicks.HasValue)
        {
            state.LastClientPositionTicks = Math.Max(0, positionTicks.Value);
        }

        var builder = new StringBuilder();
        builder.AppendLine("WEBVTT");
        builder.AppendLine();
        builder.AppendLine("NOTE Auto Generate Captions live endpoint");
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"NOTE session={sessionId} status={state.Status}"));
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"NOTE generatedThroughTicks={state.GeneratedThroughTicks}"));
        builder.AppendLine();

        foreach (CaptionCue cue in state.Cues.OrderBy(i => i.StartTicks))
        {
            builder.AppendLine(cue.Id);
            builder.Append(TicksToTimestamp(cue.StartTicks));
            builder.Append(" --> ");
            builder.AppendLine(TicksToTimestamp(cue.EndTicks));
            builder.AppendLine(cue.Text);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static CaptionSessionDto ToDto(CaptionSessionState state)
    {
        return new CaptionSessionDto
        {
            SessionId = state.SessionId,
            ItemId = state.ItemId,
            MediaSourceId = state.MediaSourceId,
            AudioStreamIndex = state.AudioStreamIndex,
            Language = state.Language,
            Status = state.Status,
            LiveVttUrl = string.Create(CultureInfo.InvariantCulture, $"/AutoGenerateCaptions/{state.SessionId}/live.vtt"),
            PollSeconds = state.PollSeconds,
            GeneratedThroughTicks = state.GeneratedThroughTicks,
            HasCachedCaptions = state.Ranges.Count > 0
        };
    }

    private static CaptionSessionStatusDto ToStatusDto(CaptionSessionState state)
    {
        CaptionSessionDto dto = ToDto(state);
        return new CaptionSessionStatusDto
        {
            SessionId = dto.SessionId,
            ItemId = dto.ItemId,
            MediaSourceId = dto.MediaSourceId,
            AudioStreamIndex = dto.AudioStreamIndex,
            Language = dto.Language,
            Status = dto.Status,
            LiveVttUrl = dto.LiveVttUrl,
            PollSeconds = dto.PollSeconds,
            GeneratedThroughTicks = dto.GeneratedThroughTicks,
            HasCachedCaptions = dto.HasCachedCaptions,
            Ranges = state.Ranges.ToArray(),
            Message = state.Message
        };
    }

    private static string GetDisplayName(Video video)
    {
        return video switch
        {
            Episode episode when !string.IsNullOrWhiteSpace(episode.SeriesName) => string.Create(
                CultureInfo.InvariantCulture,
                $"{episode.SeriesName} - S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00} - {episode.Name}"),
            Movie movie => movie.Name,
            _ => video.Name
        };
    }

    private static string TicksToTimestamp(long ticks)
    {
        TimeSpan time = TimeSpan.FromTicks(Math.Max(0, ticks));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    }

    private sealed class CaptionSessionState
    {
        public Guid SessionId { get; init; }

        public Guid ItemId { get; init; }

        public string ItemName { get; init; } = string.Empty;

        public string? MediaPath { get; init; }

        public string? MediaSourceId { get; init; }

        public int AudioStreamIndex { get; init; }

        public string Language { get; init; } = "auto";

        public string Status { get; set; } = CaptionSessionStatuses.WarmingUp;

        public int PollSeconds { get; init; }

        public long StartPositionTicks { get; init; }

        public long GeneratedThroughTicks { get; set; }

        public long LastClientPositionTicks { get; set; }

        public string? Message { get; set; }

        public DateTimeOffset? StoppedAt { get; set; }

        public List<CaptionCacheRangeDto> Ranges { get; } = [];

        public List<CaptionCue> Cues { get; } = [];
    }

    private sealed record CaptionCue(string Id, long StartTicks, long EndTicks, string Text);
}
