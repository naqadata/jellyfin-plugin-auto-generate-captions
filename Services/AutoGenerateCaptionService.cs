using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AutoGenerateCaptions.Configuration;
using Jellyfin.Plugin.AutoGenerateCaptions.Models;
using MediaBrowser.Common.Configuration;
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
    private const int GenerationPipelineVersion = 10;
    private static readonly Regex TimestampRegex = new(@"^(?<start>\d\d:\d\d:\d\d\.\d\d\d)\s+-->\s+(?<end>\d\d:\d\d:\d\d\.\d\d\d)", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<Guid, CaptionSessionState> _sessions = new();
    private readonly IApplicationPaths _applicationPaths;
    private readonly ResidentWhisperWorker _residentWhisperWorker;
    private readonly RemoteCaptionWorkerClient _remoteCaptionWorkerClient;
    private readonly ILogger<AutoGenerateCaptionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoGenerateCaptionService"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="residentWhisperWorker">Resident Whisper worker.</param>
    /// <param name="remoteCaptionWorkerClient">Remote caption worker client.</param>
    /// <param name="logger">Logger.</param>
    public AutoGenerateCaptionService(
        IApplicationPaths applicationPaths,
        ResidentWhisperWorker residentWhisperWorker,
        RemoteCaptionWorkerClient remoteCaptionWorkerClient,
        ILogger<AutoGenerateCaptionService> logger)
    {
        _applicationPaths = applicationPaths;
        _residentWhisperWorker = residentWhisperWorker;
        _remoteCaptionWorkerClient = remoteCaptionWorkerClient;
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
            Message = "Session created. Preparing caption generation."
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
            "Auto-caption worker policy for session {SessionId}: generationPipelineVersion={GenerationPipelineVersion}; primaryModel={PrimaryModel}; fallbackModel={FallbackModel}; preferredBackend={PreferredBackend}; allowCpuFallback={AllowCpuFallback}; remoteEnabled={RemoteEnabled}; remoteWorkerUrl={RemoteWorkerUrl}; remoteWorkerModel={RemoteWorkerModel}; remoteFallbackToLocal={RemoteWorkerFallbackToLocal}; initialChunkSeconds={InitialChunkSeconds}; chunkSeconds={ChunkSeconds}; chunkOverlapSeconds={ChunkOverlapSeconds}; lookaheadSeconds={LookaheadSeconds}; vadThreshold={VadThreshold}; enableRegrouping={EnableRegrouping}; regroupSplitGapSeconds={RegroupSplitGapSeconds}; maxCueCharacters={MaxCueCharacters}; maxCueWords={MaxCueWords}; maxCueDurationSeconds={MaxCueDurationSeconds}; cachePartialResults={CachePartialResults}; promoteCompletedSubtitles={PromoteCompletedSubtitles}; promotableModels={PromotableModels}",
            sessionId,
            GenerationPipelineVersion,
            config.PrimaryModel,
            config.FallbackModel,
            config.PreferredBackend,
            config.AllowCpuFallback,
            config.EnableRemoteWorker,
            config.RemoteWorkerUrl,
            config.RemoteWorkerModel,
            config.RemoteWorkerFallbackToLocal,
            config.InitialChunkSeconds,
            config.ChunkSeconds,
            config.ChunkOverlapSeconds,
            config.LookaheadSeconds,
            config.VadThreshold,
            config.EnableRegrouping,
            config.RegroupSplitGapSeconds,
            config.MaxCueCharacters,
            config.MaxCueWords,
            config.MaxCueDurationSeconds,
            config.CachePartialResults,
            config.PromoteCompletedSubtitles,
            config.PromotableModels);

        if (config.EnableVerboseWorkerLogging)
        {
            _logger.LogInformation(
                "Auto-caption verbose worker logging enabled for session {SessionId}; future worker logs should include backend probe results, GPU device name/VRAM when available, model load duration, warmup duration, ffmpeg startup duration, chunk inference duration, realtime factor, and fallback reason",
                sessionId);
        }

        _ = Task.Run(() => GenerateSessionAsync(state, config));
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
        state.Cancellation.Cancel();
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

        List<CaptionCue> cues;
        lock (state.SyncRoot)
        {
            cues = state.Cues.OrderBy(i => i.StartTicks).ToList();
        }

        if (cues.Count == 0 && IsActiveGenerationStatus(state.Status))
        {
            long placeholderStartTicks = state.LastClientPositionTicks > 0
                ? state.LastClientPositionTicks
                : state.StartPositionTicks;
            long placeholderEndTicks = placeholderStartTicks + TimeSpan.FromSeconds(5).Ticks;

            builder.AppendLine("auto-caption-placeholder");
            builder.Append(TicksToTimestamp(placeholderStartTicks));
            builder.Append(" --> ");
            builder.AppendLine(TicksToTimestamp(placeholderEndTicks));
            builder.AppendLine("*** Generator spinning up - subtitles will start soon ***");
            builder.AppendLine();
        }

        foreach (CaptionCue cue in cues)
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

    private static bool IsActiveGenerationStatus(string status)
    {
        return string.Equals(status, CaptionSessionStatuses.WarmingUp, StringComparison.Ordinal)
            || string.Equals(status, CaptionSessionStatuses.Generating, StringComparison.Ordinal);
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
            HasCachedCaptions = state.HasCachedCaptions
        };
    }

    private static CaptionSessionStatusDto ToStatusDto(CaptionSessionState state)
    {
        CaptionSessionDto dto = ToDto(state);
        IReadOnlyList<CaptionCacheRangeDto> ranges;
        lock (state.SyncRoot)
        {
            ranges = state.Ranges.ToArray();
        }

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
            Ranges = ranges,
            Message = state.Message
        };
    }

    private async Task GenerateSessionAsync(CaptionSessionState state, PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(state.MediaPath) || !File.Exists(state.MediaPath))
        {
            FailSession(state, string.Create(CultureInfo.InvariantCulture, $"Media path is missing or unreadable: {state.MediaPath}"));
            return;
        }

        string cacheRoot = GetCacheRoot(config);
        string sessionDirectory = Path.Combine(cacheRoot, "sessions", state.SessionId.ToString("N"));
        Directory.CreateDirectory(sessionDirectory);
        string? persistentCacheDirectory = config.CachePartialResults
            ? GetPersistentCacheDirectory(state, config, cacheRoot)
            : null;

        if (persistentCacheDirectory is not null)
        {
            Directory.CreateDirectory(persistentCacheDirectory);
            _logger.LogInformation(
                "Auto-caption persistent cache enabled for session {SessionId}: cacheKey={CacheKey}; directory={CacheDirectory}",
                state.SessionId,
                state.CacheKey,
                persistentCacheDirectory);
            TryHydrateFromCombinedCache(state, config, persistentCacheDirectory);
        }
        else
        {
            _logger.LogInformation("Auto-caption persistent cache disabled for session {SessionId}", state.SessionId);
        }

        string workerScriptPath;
        try
        {
            workerScriptPath = EnsureWorkerScript(config, cacheRoot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-caption worker script extraction failed for session {SessionId}", state.SessionId);
            FailSession(state, ex.Message);
            return;
        }

        state.Status = CaptionSessionStatuses.Generating;
        state.Message = "Starting caption generation loop.";
        state.LastClientPositionTicks = state.StartPositionTicks;

        double nextStartSeconds = Math.Max(0, state.StartPositionTicks / (double)TicksPerSecond);
        int initialChunkSeconds = Math.Clamp(config.InitialChunkSeconds, 3, 60);
        int steadyChunkSeconds = Math.Clamp(config.ChunkSeconds, 5, 120);
        int chunkOverlapSeconds = Math.Min(Math.Clamp(config.ChunkOverlapSeconds, 0, 15), steadyChunkSeconds / 2);
        int lookaheadSeconds = Math.Clamp(config.LookaheadSeconds, steadyChunkSeconds, 300);
        int idleDelaySeconds = Math.Clamp(config.PollSeconds, 1, 10);
        int chunkIndex = 0;

        _logger.LogInformation(
            "Auto-caption generation loop start for session {SessionId}: nextStartSeconds={NextStartSeconds}; initialChunkSeconds={InitialChunkSeconds}; steadyChunkSeconds={SteadyChunkSeconds}; chunkOverlapSeconds={ChunkOverlapSeconds}; lookaheadSeconds={LookaheadSeconds}; idleDelaySeconds={IdleDelaySeconds}",
            state.SessionId,
            nextStartSeconds,
            initialChunkSeconds,
            steadyChunkSeconds,
            chunkOverlapSeconds,
            lookaheadSeconds,
            idleDelaySeconds);

        try
        {
            while (!state.Cancellation.IsCancellationRequested)
            {
                long targetTicks = state.LastClientPositionTicks + TimeSpan.FromSeconds(lookaheadSeconds).Ticks;
                long nextStartTicks = TimeSpan.FromSeconds(nextStartSeconds).Ticks;
                if (state.GeneratedThroughTicks >= targetTicks && nextStartTicks >= targetTicks)
                {
                    state.Message = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Caption generation is {TicksToSeconds(state.GeneratedThroughTicks - state.LastClientPositionTicks):0.#}s ahead of playback.");
                    await Task.Delay(TimeSpan.FromSeconds(idleDelaySeconds), state.Cancellation.Token).ConfigureAwait(false);
                    continue;
                }

                long jumpThresholdTicks = TimeSpan.FromSeconds(Math.Max(steadyChunkSeconds, lookaheadSeconds / 2)).Ticks;
                if (state.LastClientPositionTicks - nextStartTicks > jumpThresholdTicks)
                {
                    long jumpTicks = Math.Max(0, state.LastClientPositionTicks);
                    _logger.LogInformation(
                        "Auto-caption generation jump for session {SessionId}: nextStartTicks={NextStartTicks}; clientPositionTicks={ClientPositionTicks}; jumpThresholdTicks={JumpThresholdTicks}",
                        state.SessionId,
                        nextStartTicks,
                        state.LastClientPositionTicks,
                        jumpThresholdTicks);
                    nextStartSeconds = TicksToSeconds(jumpTicks);
                    MarkGeneratedThrough(state, jumpTicks);
                }

                int chunkSeconds = chunkIndex == 0 ? initialChunkSeconds : steadyChunkSeconds;
                string chunkName = string.Create(CultureInfo.InvariantCulture, $"chunk-{chunkIndex:000}");
                string audioPath = Path.Combine(sessionDirectory, chunkName + ".wav");
                string vttPath = Path.Combine(sessionDirectory, chunkName + ".vtt");

                long generatedEndTicks = await GenerateChunkAsync(
                    state,
                    config,
                    workerScriptPath,
                    chunkIndex,
                    nextStartSeconds,
                    chunkSeconds,
                    chunkIndex == 0 ? 0 : chunkOverlapSeconds,
                    audioPath,
                    vttPath,
                    persistentCacheDirectory).ConfigureAwait(false);

                nextStartSeconds = TicksToSeconds(generatedEndTicks);
                chunkIndex++;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Auto-caption generation loop cancelled for session {SessionId}", state.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-caption generation loop failed for session {SessionId}", state.SessionId);
            FailSession(state, ex.Message);
        }
    }

    private async Task<long> GenerateChunkAsync(
        CaptionSessionState state,
        PluginConfiguration config,
        string workerScriptPath,
        int chunkIndex,
        double startSeconds,
        int chunkSeconds,
        int overlapSeconds,
        string audioPath,
        string vttPath,
        string? persistentCacheDirectory)
    {
        state.Message = string.Create(CultureInfo.InvariantCulture, $"Extracting caption chunk {chunkIndex}.");
        _logger.LogInformation(
            "Auto-caption chunk start for session {SessionId}: chunkIndex={ChunkIndex}; startSeconds={StartSeconds}; chunkSeconds={ChunkSeconds}; clientPositionTicks={ClientPositionTicks}; generatedThroughTicks={GeneratedThroughTicks}",
            state.SessionId,
            chunkIndex,
            startSeconds,
            chunkSeconds,
            state.LastClientPositionTicks,
            state.GeneratedThroughTicks);

        Stopwatch stopwatch = Stopwatch.StartNew();
        long startTicks = TimeSpan.FromSeconds(startSeconds).Ticks;
        long endTicks = TimeSpan.FromSeconds(startSeconds + chunkSeconds).Ticks;
        double extractStartSeconds = Math.Max(0, startSeconds - overlapSeconds);
        double extractDurationSeconds = chunkSeconds + (startSeconds - extractStartSeconds);
        long replacementStartTicks = TimeSpan.FromSeconds(extractStartSeconds).Ticks;

        long? cachedEndTicks = TryUseCachedChunk(state, persistentCacheDirectory, chunkIndex, startTicks, endTicks, stopwatch);
        if (cachedEndTicks.HasValue)
        {
            return cachedEndTicks.Value;
        }

        await ExtractAudioChunkAsync(state, config, extractStartSeconds, extractDurationSeconds, audioPath).ConfigureAwait(false);

        if (state.Cancellation.IsCancellationRequested)
        {
            return endTicks;
        }

        state.Message = string.Create(CultureInfo.InvariantCulture, $"Transcribing caption chunk {chunkIndex}.");
        TranscriptionResult transcription = await RunTranscriptionWorkerAsync(state, config, workerScriptPath, audioPath, vttPath, extractStartSeconds).ConfigureAwait(false);

        if (state.Cancellation.IsCancellationRequested)
        {
            return endTicks;
        }

        IReadOnlyList<CaptionCue> cues = PrepareChunkCues(ParseVtt(vttPath, chunkIndex), startTicks, endTicks);
        AddChunkResult(state, cues, startTicks, endTicks);

        if (persistentCacheDirectory is not null)
        {
            WriteChunkCache(state, persistentCacheDirectory, chunkIndex, startTicks, endTicks, cues);
            RebuildCombinedCacheVtt(state, config, persistentCacheDirectory, transcription.Model);
        }

        state.Message = cues.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Generated {cues.Count} cues for chunk {chunkIndex}.")
            : string.Create(CultureInfo.InvariantCulture, $"Worker completed but produced no cues for chunk {chunkIndex}.");

        stopwatch.Stop();
        double realtimeFactor = chunkSeconds > 0 ? chunkSeconds / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds) : 0;
        _logger.LogInformation(
            "Auto-caption chunk complete for session {SessionId}: chunkIndex={ChunkIndex}; cues={CueCount}; rangeStartTicks={RangeStartTicks}; rangeEndTicks={RangeEndTicks}; extractStartSeconds={ExtractStartSeconds}; extractDurationSeconds={ExtractDurationSeconds}; replacementStartTicks={ReplacementStartTicks}; generatedThroughTicks={GeneratedThroughTicks}; elapsedMs={ElapsedMs}; realtimeFactor={RealtimeFactor}; vtt={VttPath}",
            state.SessionId,
            chunkIndex,
            cues.Count,
            startTicks,
            endTicks,
            extractStartSeconds,
            extractDurationSeconds,
            replacementStartTicks,
            state.GeneratedThroughTicks,
            stopwatch.ElapsedMilliseconds,
            realtimeFactor,
            vttPath);

        return endTicks;
    }

    private long? TryUseCachedChunk(
        CaptionSessionState state,
        string? persistentCacheDirectory,
        int chunkIndex,
        long startTicks,
        long endTicks,
        Stopwatch stopwatch)
    {
        if (persistentCacheDirectory is null)
        {
            return null;
        }

        CacheFileRange? cachedRange = FindCachedRange(persistentCacheDirectory, startTicks, endTicks);
        if (cachedRange is null)
        {
            _logger.LogInformation(
                "Auto-caption cache miss for session {SessionId}: chunkIndex={ChunkIndex}; rangeStartTicks={RangeStartTicks}; rangeEndTicks={RangeEndTicks}; cacheDirectory={CacheDirectory}",
                state.SessionId,
                chunkIndex,
                startTicks,
                endTicks,
                persistentCacheDirectory);
            return null;
        }

        try
        {
            IReadOnlyList<CaptionCue> cues = [];
            if (HasCachedRange(state, cachedRange.StartTicks, cachedRange.EndTicks))
            {
                MarkGeneratedThrough(state, cachedRange.EndTicks);
            }
            else
            {
                cues = ParseVtt(cachedRange.Path, chunkIndex);
                AddChunkResult(state, cues, cachedRange.StartTicks, cachedRange.EndTicks);
            }

            state.HasCachedCaptions = true;
            state.Status = CaptionSessionStatuses.Generating;
            int cueCount = cues.Count > 0 ? cues.Count : CountCuesInRange(state, cachedRange.StartTicks, cachedRange.EndTicks);
            state.Message = cueCount > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Loaded {cueCount} cached cues for chunk {chunkIndex}.")
                : string.Create(CultureInfo.InvariantCulture, $"Loaded cached silent chunk {chunkIndex}.");

            stopwatch.Stop();
            double chunkSeconds = TicksToSeconds(endTicks - startTicks);
            double realtimeFactor = chunkSeconds > 0 ? chunkSeconds / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds) : 0;
            _logger.LogInformation(
                "Auto-caption cache hit for session {SessionId}: chunkIndex={ChunkIndex}; cues={CueCount}; rangeStartTicks={RangeStartTicks}; rangeEndTicks={RangeEndTicks}; generatedThroughTicks={GeneratedThroughTicks}; elapsedMs={ElapsedMs}; realtimeFactor={RealtimeFactor}; cachePath={CachePath}",
                state.SessionId,
                chunkIndex,
                cueCount,
                cachedRange.StartTicks,
                cachedRange.EndTicks,
                state.GeneratedThroughTicks,
                stopwatch.ElapsedMilliseconds,
                realtimeFactor,
                cachedRange.Path);
            return cachedRange.EndTicks;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Auto-caption cache read failed for session {SessionId}: chunkIndex={ChunkIndex}; cachePath={CachePath}. Regenerating chunk.",
                state.SessionId,
                chunkIndex,
                cachedRange.Path);
            return null;
        }
    }

    private static CacheFileRange? FindCachedRange(string persistentCacheDirectory, long startTicks, long endTicks)
    {
        if (!Directory.Exists(persistentCacheDirectory))
        {
            return null;
        }

        CacheFileRange? best = null;
        foreach (string path in Directory.EnumerateFiles(persistentCacheDirectory, "*.vtt"))
        {
            CacheFileRange? range = TryParseCacheFileRange(path);
            if (range is null)
            {
                continue;
            }

            bool exact = range.StartTicks == startTicks && range.EndTicks == endTicks;
            bool coversRequestedStart = range.StartTicks <= startTicks && range.EndTicks > startTicks;
            if (!exact && !coversRequestedStart)
            {
                continue;
            }

            if (best is null
                || exact
                || range.StartTicks > best.StartTicks
                || (range.StartTicks == best.StartTicks && range.EndTicks > best.EndTicks))
            {
                best = range;
            }
        }

        return best;
    }

    private static CacheFileRange? TryParseCacheFileRange(string path)
    {
        string fileName = Path.GetFileNameWithoutExtension(path);
        string[] parts = fileName.Split('-', 2);
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long startTicks)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long endTicks)
            || endTicks <= startTicks)
        {
            return null;
        }

        return new CacheFileRange(path, startTicks, endTicks);
    }

    private void TryHydrateFromCombinedCache(CaptionSessionState state, PluginConfiguration config, string persistentCacheDirectory)
    {
        string model = GetConfiguredCacheModel(config);
        if (!IsPromotableModel(config, model))
        {
            _logger.LogInformation(
                "Auto-caption combined cache hydrate skipped for session {SessionId}: model={Model}; promotableModels={PromotableModels}",
                state.SessionId,
                model,
                config.PromotableModels);
            return;
        }

        string combinedPath = GetCombinedVttPath(persistentCacheDirectory);
        if (!File.Exists(combinedPath))
        {
            IReadOnlyList<CacheFileRange> ranges = GetCachedChunkRanges(persistentCacheDirectory);
            if (ranges.Count == 0)
            {
                return;
            }

            RebuildCombinedCacheVtt(state, config, persistentCacheDirectory, model);
        }

        if (!File.Exists(combinedPath))
        {
            return;
        }

        try
        {
            IReadOnlyList<CaptionCue> cues = ParseVtt(combinedPath, 0);
            IReadOnlyList<CacheFileRange> ranges = GetCachedChunkRanges(persistentCacheDirectory);
            lock (state.SyncRoot)
            {
                state.Cues.Clear();
                state.Cues.AddRange(cues);
                state.Ranges.Clear();
                foreach (CacheFileRange range in ranges)
                {
                    state.Ranges.Add(new CaptionCacheRangeDto
                    {
                        StartTicks = range.StartTicks,
                        EndTicks = range.EndTicks
                    });
                }
            }

            state.HasCachedCaptions = cues.Count > 0 || ranges.Count > 0;
            _logger.LogInformation(
                "Auto-caption combined cache hydrated for session {SessionId}: cues={CueCount}; ranges={RangeCount}; combinedPath={CombinedPath}; bytes={Bytes}",
                state.SessionId,
                cues.Count,
                ranges.Count,
                combinedPath,
                new FileInfo(combinedPath).Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-caption combined cache hydrate failed for session {SessionId}: combinedPath={CombinedPath}", state.SessionId, combinedPath);
        }
    }

    private void RebuildCombinedCacheVtt(CaptionSessionState state, PluginConfiguration config, string persistentCacheDirectory, string model)
    {
        try
        {
            string combinedPath = GetCombinedVttPath(persistentCacheDirectory);
            if (!IsPromotableModel(config, model))
            {
                if (File.Exists(combinedPath))
                {
                    File.Delete(combinedPath);
                }

                _logger.LogInformation(
                    "Auto-caption combined cache skipped for session {SessionId}: model={Model}; promotableModels={PromotableModels}",
                    state.SessionId,
                    model,
                    config.PromotableModels);
                return;
            }

            IReadOnlyList<CacheFileRange> ranges = GetCachedChunkRanges(persistentCacheDirectory);
            var cues = new List<CaptionCue>();
            for (int i = 0; i < ranges.Count; i++)
            {
                MergeChunkCues(cues, ParseVtt(ranges[i].Path, i), ranges[i].StartTicks, ranges[i].EndTicks);
            }

            WriteVtt(combinedPath, cues);
            _logger.LogInformation(
                "Auto-caption combined cache written for session {SessionId}: cues={CueCount}; ranges={RangeCount}; combinedPath={CombinedPath}; bytes={Bytes}",
                state.SessionId,
                cues.Count,
                ranges.Count,
                combinedPath,
                new FileInfo(combinedPath).Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-caption combined cache write failed for session {SessionId}: directory={CacheDirectory}", state.SessionId, persistentCacheDirectory);
        }
    }

    private static void AddChunkResult(CaptionSessionState state, IReadOnlyList<CaptionCue> cues, long startTicks, long endTicks)
    {
        lock (state.SyncRoot)
        {
            if (cues.Count > 0)
            {
                MergeChunkCues(state.Cues, cues, startTicks, endTicks);
            }

            if (!state.Ranges.Any(i => i.StartTicks == startTicks && i.EndTicks == endTicks))
            {
                state.Ranges.Add(new CaptionCacheRangeDto
                {
                    StartTicks = startTicks,
                    EndTicks = endTicks
                });
            }
        }

        MarkGeneratedThrough(state, endTicks);
        state.Status = CaptionSessionStatuses.Generating;
    }

    private void WriteChunkCache(
        CaptionSessionState state,
        string persistentCacheDirectory,
        int chunkIndex,
        long startTicks,
        long endTicks,
        IReadOnlyList<CaptionCue> cues)
    {
        string cachePath = GetCachedVttPath(persistentCacheDirectory, startTicks, endTicks);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        WriteVtt(cachePath, cues);
        _logger.LogInformation(
            "Auto-caption cache write for session {SessionId}: chunkIndex={ChunkIndex}; rangeStartTicks={RangeStartTicks}; rangeEndTicks={RangeEndTicks}; cachePath={CachePath}; bytes={Bytes}",
            state.SessionId,
            chunkIndex,
            startTicks,
            endTicks,
            cachePath,
            new FileInfo(cachePath).Length);
    }

    private static void MergeChunkCues(List<CaptionCue> target, IReadOnlyList<CaptionCue> cues, long startTicks, long endTicks)
    {
        if (cues.Count == 0)
        {
            return;
        }

        CaptionCue[] preservedBoundaryCues = target
            .Where(i => i.StartTicks < startTicks && i.EndTicks > startTicks && i.StartTicks < endTicks)
            .Select(i => i with
            {
                Id = i.Id + "-pre",
                EndTicks = startTicks
            })
            .Where(i => i.EndTicks > i.StartTicks)
            .ToArray();

        target.RemoveAll(i => i.EndTicks > startTicks && i.StartTicks < endTicks);

        foreach (CaptionCue cue in preservedBoundaryCues.Concat(cues))
        {
            if (!target.Any(i => i.StartTicks == cue.StartTicks && i.EndTicks == cue.EndTicks && string.Equals(i.Text, cue.Text, StringComparison.Ordinal)))
            {
                target.Add(cue);
            }
        }
    }

    private static bool HasCachedRange(CaptionSessionState state, long startTicks, long endTicks)
    {
        lock (state.SyncRoot)
        {
            return state.Ranges.Any(i => i.StartTicks == startTicks && i.EndTicks == endTicks);
        }
    }

    private static int CountCuesInRange(CaptionSessionState state, long startTicks, long endTicks)
    {
        lock (state.SyncRoot)
        {
            return state.Cues.Count(i => i.StartTicks >= startTicks && i.StartTicks < endTicks);
        }
    }

    private static void MarkGeneratedThrough(CaptionSessionState state, long endTicks)
    {
        state.GeneratedThroughTicks = Math.Max(state.GeneratedThroughTicks, endTicks);
    }

    private static IReadOnlyList<CaptionCue> PrepareChunkCues(IReadOnlyList<CaptionCue> cues, long startTicks, long endTicks)
    {
        return cues
            .Where(i => i.EndTicks > startTicks && i.StartTicks < endTicks)
            .Select(i => i with { Text = NormalizeCueText(i.Text) })
            .Select(i => i with
            {
                StartTicks = Math.Max(i.StartTicks, startTicks),
                EndTicks = Math.Min(i.EndTicks, endTicks)
            })
            .Where(i => i.EndTicks > i.StartTicks)
            .OrderBy(i => i.StartTicks)
            .ThenBy(i => i.EndTicks)
            .ToArray();
    }

    private static string NormalizeCueText(string text)
    {
        return WhitespaceRegex.Replace(text.Trim(), " ");
    }

    private async Task ExtractAudioChunkAsync(CaptionSessionState state, PluginConfiguration config, double startSeconds, double chunkSeconds, string audioPath)
    {
        string ffmpegPath = string.IsNullOrWhiteSpace(config.FfmpegPath) ? "ffmpeg" : config.FfmpegPath;
        string requestedMap = state.AudioStreamIndex > 0
            ? string.Create(CultureInfo.InvariantCulture, $"0:{state.AudioStreamIndex}")
            : "0:a:0";
        List<string> args = BuildFfmpegAudioExtractArgs(state.MediaPath!, requestedMap, startSeconds, chunkSeconds, audioPath);

        _logger.LogInformation(
            "Auto-caption ffmpeg extraction start for session {SessionId}: ffmpeg={FfmpegPath}; media={MediaPath}; audioStreamIndex={AudioStreamIndex}; map={StreamMap}; startSeconds={StartSeconds}; chunkSeconds={ChunkSeconds}; output={AudioPath}",
            state.SessionId,
            ffmpegPath,
            state.MediaPath,
            state.AudioStreamIndex,
            requestedMap,
            startSeconds,
            chunkSeconds,
            audioPath);

        Stopwatch stopwatch = Stopwatch.StartNew();
        ProcessResult result = await RunProcessAsync(ffmpegPath, args, state.Cancellation.Token).ConfigureAwait(false);
        stopwatch.Stop();

        LogProcessOutput(state.SessionId, "ffmpeg", result);
        _logger.LogInformation(
            "Auto-caption ffmpeg extraction complete for session {SessionId}: exitCode={ExitCode}; elapsedMs={ElapsedMs}; outputBytes={OutputBytes}",
            state.SessionId,
            result.ExitCode,
            stopwatch.ElapsedMilliseconds,
            File.Exists(audioPath) ? new FileInfo(audioPath).Length : 0);

        if (result.ExitCode != 0 || !File.Exists(audioPath))
        {
            string fallbackMap = "0:a:0";
            if (!string.Equals(requestedMap, fallbackMap, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Auto-caption ffmpeg requested stream map failed for session {SessionId}: requestedMap={RequestedMap}; exitCode={ExitCode}. Retrying with fallbackMap={FallbackMap}",
                    state.SessionId,
                    requestedMap,
                    result.ExitCode,
                    fallbackMap);

                if (File.Exists(audioPath))
                {
                    File.Delete(audioPath);
                }

                args = BuildFfmpegAudioExtractArgs(state.MediaPath!, fallbackMap, startSeconds, chunkSeconds, audioPath);
                stopwatch.Restart();
                result = await RunProcessAsync(ffmpegPath, args, state.Cancellation.Token).ConfigureAwait(false);
                stopwatch.Stop();

                LogProcessOutput(state.SessionId, "ffmpeg-fallback", result);
                _logger.LogInformation(
                    "Auto-caption ffmpeg fallback extraction complete for session {SessionId}: fallbackMap={FallbackMap}; exitCode={ExitCode}; elapsedMs={ElapsedMs}; outputBytes={OutputBytes}",
                    state.SessionId,
                    fallbackMap,
                    result.ExitCode,
                    stopwatch.ElapsedMilliseconds,
                    File.Exists(audioPath) ? new FileInfo(audioPath).Length : 0);
            }
        }

        if (result.ExitCode != 0 || !File.Exists(audioPath))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"ffmpeg extraction failed with exit code {result.ExitCode}"));
        }
    }

    private static List<string> BuildFfmpegAudioExtractArgs(string mediaPath, string streamMap, double startSeconds, double chunkSeconds, string audioPath)
    {
        return
        [
            "-hide_banner",
            "-y",
            "-ss",
            startSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-t",
            chunkSeconds.ToString(CultureInfo.InvariantCulture),
            "-i",
            mediaPath,
            "-vn",
            "-map",
            streamMap,
            "-ac",
            "1",
            "-ar",
            "16000",
            "-f",
            "wav",
            audioPath
        ];
    }

    private async Task<TranscriptionResult> RunTranscriptionWorkerAsync(CaptionSessionState state, PluginConfiguration config, string workerScriptPath, string audioPath, string vttPath, double startSeconds)
    {
        if (config.EnableRemoteWorker && !string.IsNullOrWhiteSpace(config.RemoteWorkerUrl))
        {
            string remoteModel = string.IsNullOrWhiteSpace(config.RemoteWorkerModel) ? config.PrimaryModel : config.RemoteWorkerModel.Trim();
            _logger.LogInformation(
                "Auto-caption remote worker start for session {SessionId}: worker={WorkerUrl}; model={Model}; audio={AudioPath}; output={VttPath}",
                state.SessionId,
                config.RemoteWorkerUrl,
                remoteModel,
                audioPath,
                vttPath);

            bool completed = await _remoteCaptionWorkerClient.TryTranscribeAsync(
                config,
                state.SessionId,
                audioPath,
                vttPath,
                startSeconds,
                state.Language,
                state.Cancellation.Token).ConfigureAwait(false);

            if (completed)
            {
                return new TranscriptionResult(remoteModel);
            }

            if (!config.RemoteWorkerFallbackToLocal)
            {
                throw new InvalidOperationException("Remote caption worker is unavailable and local fallback is disabled.");
            }

            _logger.LogWarning(
                "Auto-caption remote worker unavailable before job start for session {SessionId}; falling back to local transcription.",
                state.SessionId);
        }

        return await RunWhisperWorkerAsync(state, config, workerScriptPath, audioPath, vttPath, startSeconds).ConfigureAwait(false);
    }

    private async Task<TranscriptionResult> RunWhisperWorkerAsync(CaptionSessionState state, PluginConfiguration config, string workerScriptPath, string audioPath, string vttPath, double startSeconds)
    {
        try
        {
            Stopwatch residentStopwatch = Stopwatch.StartNew();
            ResidentWhisperWorker.WorkerResponse response = await _residentWhisperWorker.TranscribeAsync(
                config,
                audioPath,
                vttPath,
                startSeconds,
                state.Language,
                state.Cancellation.Token).ConfigureAwait(false);
            residentStopwatch.Stop();

            _logger.LogInformation(
                "Auto-caption resident whisper worker complete for session {SessionId}: model={Model}; device={Device}; segments={SegmentCount}; workerElapsedSeconds={WorkerElapsedSeconds}; elapsedMs={ElapsedMs}; vttBytes={VttBytes}",
                state.SessionId,
                response.Model,
                response.Device,
                response.SegmentCount,
                response.ElapsedSeconds,
                residentStopwatch.ElapsedMilliseconds,
                File.Exists(vttPath) ? new FileInfo(vttPath).Length : 0);

            if (!File.Exists(vttPath))
            {
                throw new InvalidOperationException("Resident Whisper worker completed without writing VTT output.");
            }

            return new TranscriptionResult(string.IsNullOrWhiteSpace(response.Model) ? config.PrimaryModel : response.Model);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (state.Cancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(state.Cancellation.Token);
            }

            _logger.LogWarning(ex, "Auto-caption resident whisper worker failed for session {SessionId}; falling back to per-chunk process.", state.SessionId);
        }

        string pythonPath = string.IsNullOrWhiteSpace(config.PythonPath) ? "python3" : config.PythonPath;
        var args = new List<string>
        {
            workerScriptPath,
            "--audio",
            audioPath,
            "--output",
            vttPath,
            "--model",
            config.PrimaryModel,
            "--fallback-model",
            config.FallbackModel,
            "--language",
            state.Language,
            "--backend",
            config.PreferredBackend,
            "--offset-seconds",
            startSeconds.ToString("0.###", CultureInfo.InvariantCulture)
        };

        if (config.AllowCpuFallback)
        {
            args.Add("--allow-cpu-fallback");
        }

        ResidentWhisperWorker.AddQualityArguments(args, config);

        _logger.LogInformation(
            "Auto-caption whisper worker start for session {SessionId}: python={PythonPath}; script={WorkerScriptPath}; model={Model}; fallbackModel={FallbackModel}; backend={Backend}; vadThreshold={VadThreshold}; enableRegrouping={EnableRegrouping}; audio={AudioPath}; output={VttPath}",
            state.SessionId,
            pythonPath,
            workerScriptPath,
            config.PrimaryModel,
            config.FallbackModel,
            config.PreferredBackend,
            config.VadThreshold,
            config.EnableRegrouping,
            audioPath,
            vttPath);

        Stopwatch stopwatch = Stopwatch.StartNew();
        ProcessResult result = await RunProcessAsync(pythonPath, args, state.Cancellation.Token).ConfigureAwait(false);
        stopwatch.Stop();
        LogProcessOutput(state.SessionId, "whisper", result);
        _logger.LogInformation(
            "Auto-caption whisper worker complete for session {SessionId}: exitCode={ExitCode}; elapsedMs={ElapsedMs}; vttBytes={VttBytes}",
            state.SessionId,
            result.ExitCode,
            stopwatch.ElapsedMilliseconds,
            File.Exists(vttPath) ? new FileInfo(vttPath).Length : 0);

        if (result.ExitCode != 0 || !File.Exists(vttPath))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Whisper worker failed with exit code {result.ExitCode}"));
        }

        return new TranscriptionResult(config.PrimaryModel);
    }

    private string GetCacheRoot(PluginConfiguration config)
    {
        string configured = config.CacheDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return configured;
        }

        string fallback = Path.Combine(_applicationPaths.DataPath, "auto-generate-captions");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static string GetPersistentCacheDirectory(CaptionSessionState state, PluginConfiguration config, string cacheRoot)
    {
        string fingerprint = GetMediaFingerprint(state.MediaPath);
        string keyMaterial = string.Join(
            '|',
            GenerationPipelineVersion.ToString(CultureInfo.InvariantCulture),
            state.ItemId.ToString("N"),
            state.MediaSourceId ?? string.Empty,
            state.AudioStreamIndex.ToString(CultureInfo.InvariantCulture),
            state.Language,
            config.PrimaryModel,
            config.FallbackModel,
            config.PreferredBackend,
            Math.Clamp(config.ChunkOverlapSeconds, 0, 15).ToString(CultureInfo.InvariantCulture),
            Math.Clamp(config.VadThreshold, 0.05, 0.95).ToString("0.###", CultureInfo.InvariantCulture),
            config.EnableRegrouping.ToString(CultureInfo.InvariantCulture),
            Math.Clamp(config.RegroupSplitGapSeconds, 0.1, 2.0).ToString("0.###", CultureInfo.InvariantCulture),
            Math.Clamp(config.MaxCueCharacters, 20, 180).ToString(CultureInfo.InvariantCulture),
            Math.Clamp(config.MaxCueWords, 3, 40).ToString(CultureInfo.InvariantCulture),
            Math.Clamp(config.MaxCueDurationSeconds, 1.0, 15.0).ToString("0.###", CultureInfo.InvariantCulture),
            GetRemoteCacheFingerprint(config),
            GetLocalPunctuationCacheFingerprint(config),
            fingerprint);

        state.CacheKey = HashString(keyMaterial);
        return Path.Combine(cacheRoot, "media", state.CacheKey);
    }

    private static string GetCachedVttPath(string persistentCacheDirectory, long startTicks, long endTicks)
    {
        string fileName = string.Create(CultureInfo.InvariantCulture, $"{startTicks:D18}-{endTicks:D18}.vtt");
        return Path.Combine(persistentCacheDirectory, fileName);
    }

    private static string GetCombinedVttPath(string persistentCacheDirectory)
    {
        return Path.Combine(persistentCacheDirectory, "combined.vtt");
    }

    private static IReadOnlyList<CacheFileRange> GetCachedChunkRanges(string persistentCacheDirectory)
    {
        if (!Directory.Exists(persistentCacheDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(persistentCacheDirectory, "*.vtt")
            .Select(TryParseCacheFileRange)
            .OfType<CacheFileRange>()
            .OrderBy(i => i.StartTicks)
            .ThenBy(i => i.EndTicks)
            .ToArray();
    }

    private static string GetMediaFingerprint(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
        {
            return mediaPath ?? string.Empty;
        }

        var fileInfo = new FileInfo(mediaPath);
        return string.Join(
            '|',
            fileInfo.FullName,
            fileInfo.Length.ToString(CultureInfo.InvariantCulture),
            fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    private static string HashString(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GetRemoteCacheFingerprint(PluginConfiguration config)
    {
        if (!config.EnableRemoteWorker || string.IsNullOrWhiteSpace(config.RemoteWorkerUrl))
        {
            return "local";
        }

        string remoteModel = string.IsNullOrWhiteSpace(config.RemoteWorkerModel)
            ? config.PrimaryModel
            : config.RemoteWorkerModel.Trim();
        return string.Join('|', "remote", config.RemoteWorkerUrl.Trim(), remoteModel);
    }

    private static string GetLocalPunctuationCacheFingerprint(PluginConfiguration config)
    {
        if (!config.EnableRemoteWorker || !config.EnableLocalPunctuation)
        {
            return "punctuation:off";
        }

        string punctuationModel = string.IsNullOrWhiteSpace(config.LocalPunctuationModel)
            ? "oliverguhr/fullstop-punctuation-multilang-large"
            : config.LocalPunctuationModel.Trim();
        return string.Join('|', "punctuation:on", punctuationModel);
    }

    private static bool IsPromotableModel(PluginConfiguration config, string model)
    {
        if (string.IsNullOrWhiteSpace(config.PromotableModels))
        {
            return true;
        }

        string normalizedModel = NormalizeModelName(model);
        return config.PromotableModels
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeModelName)
            .Any(i => string.Equals(i, normalizedModel, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetConfiguredCacheModel(PluginConfiguration config)
    {
        if (config.EnableRemoteWorker && !string.IsNullOrWhiteSpace(config.RemoteWorkerUrl))
        {
            return string.IsNullOrWhiteSpace(config.RemoteWorkerModel)
                ? config.PrimaryModel
                : config.RemoteWorkerModel.Trim();
        }

        return config.PrimaryModel;
    }

    private static string NormalizeModelName(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.Empty;
        }

        string normalized = model.Trim().Replace('\\', '/');
        int slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
        {
            normalized = normalized[(slashIndex + 1)..];
        }

        return normalized;
    }

    private static string EnsureWorkerScript(PluginConfiguration config, string cacheRoot)
    {
        if (!string.IsNullOrWhiteSpace(config.WorkerScriptPath))
        {
            return config.WorkerScriptPath;
        }

        string scriptPath = Path.Combine(cacheRoot, "workers", "whisper_chunk.py");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);

        Assembly assembly = typeof(AutoGenerateCaptionService).Assembly;
        string resourceName = assembly
            .GetManifestResourceNames()
            .First(i => i.EndsWith("Workers.whisper_chunk.py", StringComparison.Ordinal));

        using Stream resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Bundled whisper worker resource not found.");
        using FileStream file = File.Create(scriptPath);
        resource.CopyTo(file);
        return scriptPath;
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                error.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private void LogProcessOutput(Guid sessionId, string name, ProcessResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _logger.LogInformation("Auto-caption {ProcessName} stdout for session {SessionId}: {Line}", name, sessionId, line);
            }
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            foreach (string line in result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _logger.LogWarning("Auto-caption {ProcessName} stderr for session {SessionId}: {Line}", name, sessionId, line);
            }
        }
    }

    private static IReadOnlyList<CaptionCue> ParseVtt(string vttPath, int chunkIndex)
    {
        var cues = new List<CaptionCue>();
        string[] lines = File.ReadAllLines(vttPath);
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = TimestampRegex.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            string id = i > 0 && !string.IsNullOrWhiteSpace(lines[i - 1]) ? lines[i - 1].Trim() : string.Create(CultureInfo.InvariantCulture, $"cue-{cues.Count}");
            var textLines = new List<string>();
            for (int j = i + 1; j < lines.Length && !string.IsNullOrWhiteSpace(lines[j]); j++)
            {
                textLines.Add(lines[j].Trim());
            }

            if (textLines.Count == 0)
            {
                continue;
            }

            cues.Add(new CaptionCue(
                string.Create(CultureInfo.InvariantCulture, $"chunk-{chunkIndex:000}-{id}"),
                TimestampToTicks(match.Groups["start"].Value),
                TimestampToTicks(match.Groups["end"].Value),
                string.Join('\n', textLines)));
        }

        return cues;
    }

    private static void WriteVtt(string vttPath, IReadOnlyList<CaptionCue> cues)
    {
        using var writer = new StreamWriter(vttPath, append: false, Encoding.UTF8);
        writer.WriteLine("WEBVTT");
        writer.WriteLine();

        int index = 0;
        foreach (CaptionCue cue in cues.OrderBy(i => i.StartTicks).ThenBy(i => i.EndTicks))
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"combined-{index:000000}"));
            writer.Write(TicksToTimestamp(cue.StartTicks));
            writer.Write(" --> ");
            writer.WriteLine(TicksToTimestamp(cue.EndTicks));
            writer.WriteLine(cue.Text);
            writer.WriteLine();
            index++;
        }
    }

    private static long TimestampToTicks(string timestamp)
    {
        string[] parts = timestamp.Split([':', '.']);
        int hours = int.Parse(parts[0], CultureInfo.InvariantCulture);
        int minutes = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int seconds = int.Parse(parts[2], CultureInfo.InvariantCulture);
        int milliseconds = int.Parse(parts[3], CultureInfo.InvariantCulture);
        return TimeSpan.FromHours(hours).Ticks
            + TimeSpan.FromMinutes(minutes).Ticks
            + TimeSpan.FromSeconds(seconds).Ticks
            + TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }

    private static double TicksToSeconds(long ticks)
    {
        return ticks / (double)TicksPerSecond;
    }

    private static void FailSession(CaptionSessionState state, string message)
    {
        state.Status = CaptionSessionStatuses.Failed;
        state.Message = message;
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

        public bool HasCachedCaptions { get; set; }

        public string? CacheKey { get; set; }

        public object SyncRoot { get; } = new();

        public CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed record CaptionCue(string Id, long StartTicks, long EndTicks, string Text);

    private sealed record CacheFileRange(string Path, long StartTicks, long EndTicks);

    private sealed record TranscriptionResult(string Model);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
