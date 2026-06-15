using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;
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
    private static readonly Regex TimestampRegex = new(@"^(?<start>\d\d:\d\d:\d\d\.\d\d\d)\s+-->\s+(?<end>\d\d:\d\d:\d\d\.\d\d\d)", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<Guid, CaptionSessionState> _sessions = new();
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<AutoGenerateCaptionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoGenerateCaptionService"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public AutoGenerateCaptionService(IApplicationPaths applicationPaths, ILogger<AutoGenerateCaptionService> logger)
    {
        _applicationPaths = applicationPaths;
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

        double nextStartSeconds = Math.Max(0, (state.StartPositionTicks / (double)TicksPerSecond) + 8);
        int initialChunkSeconds = Math.Clamp(config.InitialChunkSeconds, 3, 60);
        int steadyChunkSeconds = Math.Clamp(config.ChunkSeconds, 5, 120);
        int lookaheadSeconds = Math.Clamp(config.LookaheadSeconds, steadyChunkSeconds, 300);
        int idleDelaySeconds = Math.Clamp(config.PollSeconds, 1, 10);
        int chunkIndex = 0;

        _logger.LogInformation(
            "Auto-caption generation loop start for session {SessionId}: nextStartSeconds={NextStartSeconds}; initialChunkSeconds={InitialChunkSeconds}; steadyChunkSeconds={SteadyChunkSeconds}; lookaheadSeconds={LookaheadSeconds}; idleDelaySeconds={IdleDelaySeconds}",
            state.SessionId,
            nextStartSeconds,
            initialChunkSeconds,
            steadyChunkSeconds,
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

                int chunkSeconds = chunkIndex == 0 ? initialChunkSeconds : steadyChunkSeconds;
                string chunkName = string.Create(CultureInfo.InvariantCulture, $"chunk-{chunkIndex:000}");
                string audioPath = Path.Combine(sessionDirectory, chunkName + ".wav");
                string vttPath = Path.Combine(sessionDirectory, chunkName + ".vtt");

                await GenerateChunkAsync(
                    state,
                    config,
                    workerScriptPath,
                    chunkIndex,
                    nextStartSeconds,
                    chunkSeconds,
                    audioPath,
                    vttPath).ConfigureAwait(false);

                nextStartSeconds += chunkSeconds;
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

    private async Task GenerateChunkAsync(
        CaptionSessionState state,
        PluginConfiguration config,
        string workerScriptPath,
        int chunkIndex,
        double startSeconds,
        int chunkSeconds,
        string audioPath,
        string vttPath)
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
        await ExtractAudioChunkAsync(state, config, startSeconds, chunkSeconds, audioPath).ConfigureAwait(false);

        if (state.Cancellation.IsCancellationRequested)
        {
            return;
        }

        state.Message = string.Create(CultureInfo.InvariantCulture, $"Transcribing caption chunk {chunkIndex}.");
        await RunWhisperWorkerAsync(state, config, workerScriptPath, audioPath, vttPath, startSeconds).ConfigureAwait(false);

        if (state.Cancellation.IsCancellationRequested)
        {
            return;
        }

        IReadOnlyList<CaptionCue> cues = ParseVtt(vttPath, chunkIndex);
        long startTicks = TimeSpan.FromSeconds(startSeconds).Ticks;
        long endTicks = TimeSpan.FromSeconds(startSeconds + chunkSeconds).Ticks;
        lock (state.SyncRoot)
        {
            state.Cues.AddRange(cues);
            state.Ranges.Add(new CaptionCacheRangeDto
            {
                StartTicks = startTicks,
                EndTicks = endTicks
            });
        }

        state.GeneratedThroughTicks = Math.Max(state.GeneratedThroughTicks, endTicks);
        state.Status = CaptionSessionStatuses.Generating;
        state.Message = cues.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Generated {cues.Count} cues for chunk {chunkIndex}.")
            : string.Create(CultureInfo.InvariantCulture, $"Worker completed but produced no cues for chunk {chunkIndex}.");

        stopwatch.Stop();
        double realtimeFactor = chunkSeconds > 0 ? chunkSeconds / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds) : 0;
        _logger.LogInformation(
            "Auto-caption chunk complete for session {SessionId}: chunkIndex={ChunkIndex}; cues={CueCount}; rangeStartTicks={RangeStartTicks}; rangeEndTicks={RangeEndTicks}; generatedThroughTicks={GeneratedThroughTicks}; elapsedMs={ElapsedMs}; realtimeFactor={RealtimeFactor}; vtt={VttPath}",
            state.SessionId,
            chunkIndex,
            cues.Count,
            startTicks,
            endTicks,
            state.GeneratedThroughTicks,
            stopwatch.ElapsedMilliseconds,
            realtimeFactor,
            vttPath);
    }

    private async Task ExtractAudioChunkAsync(CaptionSessionState state, PluginConfiguration config, double startSeconds, int chunkSeconds, string audioPath)
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

    private static List<string> BuildFfmpegAudioExtractArgs(string mediaPath, string streamMap, double startSeconds, int chunkSeconds, string audioPath)
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

    private async Task RunWhisperWorkerAsync(CaptionSessionState state, PluginConfiguration config, string workerScriptPath, string audioPath, string vttPath, double startSeconds)
    {
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

        _logger.LogInformation(
            "Auto-caption whisper worker start for session {SessionId}: python={PythonPath}; script={WorkerScriptPath}; model={Model}; fallbackModel={FallbackModel}; backend={Backend}; audio={AudioPath}; output={VttPath}",
            state.SessionId,
            pythonPath,
            workerScriptPath,
            config.PrimaryModel,
            config.FallbackModel,
            config.PreferredBackend,
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

        public object SyncRoot { get; } = new();

        public CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed record CaptionCue(string Id, long StartTicks, long EndTicks, string Text);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
