using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AutoGenerateCaptions.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoGenerateCaptions.Services;

/// <summary>
/// Keeps a Whisper worker process warm and loaded for chunk transcription jobs.
/// </summary>
public sealed class ResidentWhisperWorker : IHostedService, IDisposable
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<ResidentWhisperWorker> _logger;
    private readonly SemaphoreSlim _jobLock = new(1, 1);
    private readonly object _syncRoot = new();
    private Process? _process;
    private TaskCompletionSource<WorkerResponse>? _pendingResponse;
    private string? _pendingResponseId;
    private string? _workerScriptPath;
    private string? _workerSettingsKey;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResidentWhisperWorker"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public ResidentWhisperWorker(IApplicationPaths applicationPaths, ILogger<ResidentWhisperWorker> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (string.IsNullOrWhiteSpace(config.PythonPath))
        {
            _logger.LogWarning("Auto-caption resident worker not started because PythonPath is empty.");
            return Task.CompletedTask;
        }

        try
        {
            EnsureStarted(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-caption resident worker startup failed; chunk generation will fall back to per-job worker processes.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopProcess();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Transcribes a chunk using the resident worker.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="audioPath">Audio path.</param>
    /// <param name="vttPath">Output VTT path.</param>
    /// <param name="offsetSeconds">Caption timestamp offset in seconds.</param>
    /// <param name="language">Language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Worker response.</returns>
    public async Task<WorkerResponse> TranscribeAsync(
        PluginConfiguration config,
        string audioPath,
        string vttPath,
        double offsetSeconds,
        string language,
        CancellationToken cancellationToken)
    {
        await _jobLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStarted(config);

            Process process = _process ?? throw new InvalidOperationException("Resident Whisper worker is not running.");
            string requestId = Guid.NewGuid().ToString("N");
            var responseSource = new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_syncRoot)
            {
                _pendingResponseId = requestId;
                _pendingResponse = responseSource;
            }

            var request = new
            {
                id = requestId,
                audio = audioPath,
                output = vttPath,
                offsetSeconds,
                language
            };

            string json = JsonSerializer.Serialize(request);
            await process.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);

            await using CancellationTokenRegistration registration = cancellationToken.Register(
                () => CancelActiveJob(requestId, responseSource, cancellationToken));
            return await responseSource.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_syncRoot)
            {
                _pendingResponseId = null;
                _pendingResponse = null;
            }

            _jobLock.Release();
        }
    }

    private void EnsureStarted(PluginConfiguration config)
    {
        lock (_syncRoot)
        {
            string settingsKey = GetWorkerSettingsKey(config);
            if (_process is { HasExited: false } && string.Equals(_workerSettingsKey, settingsKey, StringComparison.Ordinal))
            {
                return;
            }

            StopProcess();
            string cacheRoot = GetCacheRoot(config);
            _workerScriptPath = EnsureWorkerScript(config, cacheRoot);
            StartProcess(config, _workerScriptPath);
            _workerSettingsKey = settingsKey;
        }
    }

    private void StartProcess(PluginConfiguration config, string workerScriptPath)
    {
        string pythonPath = string.IsNullOrWhiteSpace(config.PythonPath) ? "python3" : config.PythonPath;
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };

        process.StartInfo.ArgumentList.Add(workerScriptPath);
        process.StartInfo.ArgumentList.Add("--server");
        process.StartInfo.ArgumentList.Add("--model");
        process.StartInfo.ArgumentList.Add(config.PrimaryModel);
        process.StartInfo.ArgumentList.Add("--fallback-model");
        process.StartInfo.ArgumentList.Add(config.FallbackModel);
        process.StartInfo.ArgumentList.Add("--language");
        process.StartInfo.ArgumentList.Add(config.DefaultLanguage);
        process.StartInfo.ArgumentList.Add("--backend");
        process.StartInfo.ArgumentList.Add(config.PreferredBackend);
        if (config.AllowCpuFallback)
        {
            process.StartInfo.ArgumentList.Add("--allow-cpu-fallback");
        }

        AddQualityArguments(process.StartInfo.ArgumentList, config);

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                HandleWorkerStdout(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.LogInformation("Auto-caption resident worker log: {Line}", e.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            TaskCompletionSource<WorkerResponse>? pending;
            lock (_syncRoot)
            {
                pending = _pendingResponse;
            }

            pending?.TrySetException(new InvalidOperationException("Resident Whisper worker exited."));
            _logger.LogWarning("Auto-caption resident worker exited with code {ExitCode}", process.ExitCode);
        };

        _logger.LogInformation(
            "Auto-caption resident worker start: python={PythonPath}; script={WorkerScriptPath}; primaryModel={PrimaryModel}; fallbackModel={FallbackModel}; backend={Backend}; allowCpuFallback={AllowCpuFallback}; vadThreshold={VadThreshold}; enableRegrouping={EnableRegrouping}; regroupSplitGapSeconds={RegroupSplitGapSeconds}; maxCueCharacters={MaxCueCharacters}; maxCueWords={MaxCueWords}; maxCueDurationSeconds={MaxCueDurationSeconds}",
            pythonPath,
            workerScriptPath,
            config.PrimaryModel,
            config.FallbackModel,
            config.PreferredBackend,
            config.AllowCpuFallback,
            config.VadThreshold,
            config.EnableRegrouping,
            config.RegroupSplitGapSeconds,
            config.MaxCueCharacters,
            config.MaxCueWords,
            config.MaxCueDurationSeconds);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    /// <summary>
    /// Adds transcription quality arguments consumed by the Python worker.
    /// </summary>
    /// <param name="arguments">Argument list.</param>
    /// <param name="config">Plugin configuration.</param>
    public static void AddQualityArguments(ICollection<string> arguments, PluginConfiguration config)
    {
        arguments.Add("--vad-threshold");
        arguments.Add(Math.Clamp(config.VadThreshold, 0.05, 0.95).ToString("0.###", CultureInfo.InvariantCulture));
        if (config.EnableRegrouping)
        {
            arguments.Add("--enable-regrouping");
        }

        arguments.Add("--regroup-split-gap-seconds");
        arguments.Add(Math.Clamp(config.RegroupSplitGapSeconds, 0.1, 2.0).ToString("0.###", CultureInfo.InvariantCulture));
        arguments.Add("--max-cue-characters");
        arguments.Add(Math.Clamp(config.MaxCueCharacters, 20, 180).ToString(CultureInfo.InvariantCulture));
        arguments.Add("--max-cue-words");
        arguments.Add(Math.Clamp(config.MaxCueWords, 3, 40).ToString(CultureInfo.InvariantCulture));
        arguments.Add("--max-cue-duration-seconds");
        arguments.Add(Math.Clamp(config.MaxCueDurationSeconds, 1.0, 15.0).ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static string GetWorkerSettingsKey(PluginConfiguration config)
    {
        return string.Join(
            '|',
            config.PythonPath,
            config.WorkerScriptPath,
            config.PrimaryModel,
            config.FallbackModel,
            config.DefaultLanguage,
            config.PreferredBackend,
            config.AllowCpuFallback.ToString(CultureInfo.InvariantCulture),
            Math.Clamp(config.VadThreshold, 0.05, 0.95).ToString("0.###", CultureInfo.InvariantCulture),
            config.EnableRegrouping.ToString(CultureInfo.InvariantCulture),
            Math.Clamp(config.RegroupSplitGapSeconds, 0.1, 2.0).ToString("0.###", CultureInfo.InvariantCulture),
            Math.Clamp(config.MaxCueCharacters, 20, 180).ToString(CultureInfo.InvariantCulture),
            Math.Clamp(config.MaxCueWords, 3, 40).ToString(CultureInfo.InvariantCulture),
            Math.Clamp(config.MaxCueDurationSeconds, 1.0, 15.0).ToString("0.###", CultureInfo.InvariantCulture));
    }

    private void HandleWorkerStdout(string line)
    {
        WorkerResponse? response = null;
        try
        {
            response = JsonSerializer.Deserialize<WorkerResponse>(line, JsonSerializerOptions.Web);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Auto-caption resident worker emitted non-response stdout: {Line}", line);
            return;
        }

        if (response is null || string.IsNullOrWhiteSpace(response.Id))
        {
            _logger.LogWarning("Auto-caption resident worker emitted invalid response: {Line}", line);
            return;
        }

        TaskCompletionSource<WorkerResponse>? pending;
        string? pendingId;
        lock (_syncRoot)
        {
            pending = _pendingResponse;
            pendingId = _pendingResponseId;
        }

        if (pending is null || !string.Equals(pendingId, response.Id, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Auto-caption resident worker emitted unexpected response id {ResponseId}; pendingId={PendingId}",
                response.Id,
                pendingId);
            return;
        }

        if (response.Ok)
        {
            pending.TrySetResult(response);
        }
        else
        {
            pending.TrySetException(new InvalidOperationException(response.Error ?? "Resident Whisper worker failed."));
        }
    }

    private void CancelActiveJob(string requestId, TaskCompletionSource<WorkerResponse> responseSource, CancellationToken cancellationToken)
    {
        Process? processToStop = null;
        lock (_syncRoot)
        {
            if (!string.Equals(_pendingResponseId, requestId, StringComparison.Ordinal))
            {
                responseSource.TrySetCanceled(cancellationToken);
                return;
            }

            processToStop = _process;
            _process = null;
            _pendingResponseId = null;
            _pendingResponse = null;
        }

        _logger.LogInformation("Auto-caption resident worker active job {RequestId} cancelled; stopping worker process to interrupt transcription.", requestId);
        StopProcess(processToStop);
        responseSource.TrySetCanceled(cancellationToken);
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

        Assembly assembly = typeof(ResidentWhisperWorker).Assembly;
        string resourceName = assembly
            .GetManifestResourceNames()
            .First(i => i.EndsWith("Workers.whisper_chunk.py", StringComparison.Ordinal));

        using Stream resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Bundled whisper worker resource not found.");
        using FileStream file = File.Create(scriptPath);
        resource.CopyTo(file);
        return scriptPath;
    }

    private void StopProcess()
    {
        Process? process = _process;
        _process = null;
        StopProcess(process);
    }

    private void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-caption resident worker shutdown failed.");
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopProcess();
        _jobLock.Dispose();
    }

    /// <summary>
    /// Worker response from the resident process.
    /// </summary>
    /// <param name="Id">Request id.</param>
    /// <param name="Ok">Whether the request succeeded.</param>
    /// <param name="SegmentCount">Segment count.</param>
    /// <param name="Model">Model.</param>
    /// <param name="Device">Device.</param>
    /// <param name="ElapsedSeconds">Elapsed seconds.</param>
    /// <param name="Error">Error.</param>
    public sealed record WorkerResponse(
        string Id,
        bool Ok,
        int SegmentCount,
        string? Model,
        string? Device,
        double ElapsedSeconds,
        string? Error);
}
