using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AutoGenerateCaptions.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoGenerateCaptions.Services;

/// <summary>
/// Client for an optional remote Naqafin caption worker.
/// </summary>
public sealed class RemoteCaptionWorkerClient : IDisposable
{
    private static readonly Regex FirstPersonRegex = new(@"\bi(?:('|\u2019)(m|ve|ll|d))?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = new();
    private readonly ILogger<RemoteCaptionWorkerClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteCaptionWorkerClient"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public RemoteCaptionWorkerClient(ILogger<RemoteCaptionWorkerClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempts to transcribe an audio chunk on the remote worker.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="sessionId">Caption session id.</param>
    /// <param name="audioPath">Audio file path.</param>
    /// <param name="vttPath">Output VTT file path.</param>
    /// <param name="offsetSeconds">Timestamp offset in seconds.</param>
    /// <param name="language">Language hint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when remote transcription completed; <c>false</c> when remote is unavailable before a job starts.</returns>
    public async Task<bool> TryTranscribeAsync(
        PluginConfiguration config,
        Guid sessionId,
        string audioPath,
        string vttPath,
        double offsetSeconds,
        string language,
        CancellationToken cancellationToken)
    {
        if (!TryGetBaseUri(config, out Uri? baseUri) || baseUri is null)
        {
            return false;
        }
        if (!await IsHealthyAsync(baseUri, config, sessionId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        RemoteJobResponse? job = await SubmitJobAsync(baseUri, config, sessionId, audioPath, language, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return false;
        }

        RemoteJobResponse completedJob = await WaitForJobAsync(baseUri, config, sessionId, job.JobId, cancellationToken).ConfigureAwait(false);
        RemoteTranscriptResult result = await GetResultAsync(baseUri, config, sessionId, completedJob.JobId, cancellationToken).ConfigureAwait(false);
        WriteVtt(vttPath, result.Segments, offsetSeconds);

        _logger.LogInformation(
            "Auto-caption remote worker complete for session {SessionId}: worker={WorkerUrl}; jobId={JobId}; model={Model}; language={Language}; segments={SegmentCount}; vttBytes={VttBytes}",
            sessionId,
            baseUri,
            completedJob.JobId,
            completedJob.Model,
            result.Language,
            result.Segments.Count,
            File.Exists(vttPath) ? new FileInfo(vttPath).Length : 0);

        _ = DeleteJobAsync(baseUri, config, completedJob.JobId, CancellationToken.None);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static bool TryGetBaseUri(PluginConfiguration config, out Uri? baseUri)
    {
        baseUri = null;
        string configured = config.RemoteWorkerUrl.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        if (!configured.EndsWith("/", StringComparison.Ordinal))
        {
            configured += "/";
        }

        return Uri.TryCreate(configured, UriKind.Absolute, out baseUri)
            && (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps);
    }

    private async Task<bool> IsHealthyAsync(Uri baseUri, PluginConfiguration config, Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.RemoteWorkerHealthTimeoutSeconds, 1, 30)));
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(baseUri, "health"), config);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Auto-caption remote worker health check failed for session {SessionId}: worker={WorkerUrl}; statusCode={StatusCode}",
                    sessionId,
                    baseUri,
                    response.StatusCode);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Auto-caption remote worker health check timed out for session {SessionId}: worker={WorkerUrl}", sessionId, baseUri);
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Auto-caption remote worker health check failed for session {SessionId}: worker={WorkerUrl}", sessionId, baseUri);
            return false;
        }
    }

    private async Task<RemoteJobResponse?> SubmitJobAsync(
        Uri baseUri,
        PluginConfiguration config,
        Guid sessionId,
        string audioPath,
        string language,
        CancellationToken cancellationToken)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            await using FileStream audio = File.OpenRead(audioPath);
            using var audioContent = new StreamContent(audio);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audioContent, "audio", Path.GetFileName(audioPath));
            form.Add(new StringContent(GetRemoteModel(config)), "model");
            form.Add(new StringContent(NormalizeLanguage(language)), "language");
            form.Add(new StringContent("json"), "output_format");
            form.Add(new StringContent(Math.Clamp(config.VadThreshold, 0.05, 0.95).ToString("0.###", CultureInfo.InvariantCulture)), "vad_threshold");
            form.Add(new StringContent(config.EnableRegrouping ? "true" : "false"), "enable_regrouping");
            form.Add(new StringContent(Math.Clamp(config.RegroupSplitGapSeconds, 0.1, 2.0).ToString("0.###", CultureInfo.InvariantCulture)), "regroup_split_gap_seconds");
            form.Add(new StringContent(Math.Clamp(config.MaxCueCharacters, 20, 180).ToString(CultureInfo.InvariantCulture)), "max_cue_characters");
            form.Add(new StringContent(Math.Clamp(config.MaxCueWords, 3, 40).ToString(CultureInfo.InvariantCulture)), "max_cue_words");
            form.Add(new StringContent(Math.Clamp(config.MaxCueDurationSeconds, 1.0, 15.0).ToString("0.###", CultureInfo.InvariantCulture)), "max_cue_duration_seconds");

            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, new Uri(baseUri, "v1/jobs"), config);
            request.Content = form;
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Auto-caption remote worker job submit failed for session {SessionId}: worker={WorkerUrl}; statusCode={StatusCode}; body={Body}",
                    sessionId,
                    baseUri,
                    response.StatusCode,
                    body);
                return null;
            }

            RemoteJobResponse? job = await response.Content.ReadFromJsonAsync<RemoteJobResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Auto-caption remote worker job submitted for session {SessionId}: worker={WorkerUrl}; jobId={JobId}; model={Model}",
                sessionId,
                baseUri,
                job?.JobId,
                job?.Model);
            return job;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Auto-caption remote worker job submit failed for session {SessionId}: worker={WorkerUrl}", sessionId, baseUri);
            return null;
        }
    }

    private async Task<RemoteJobResponse> WaitForJobAsync(
        Uri baseUri,
        PluginConfiguration config,
        Guid sessionId,
        string jobId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.RemoteWorkerJobTimeoutSeconds, 30, 7200)));
        TimeSpan pollDelay = TimeSpan.FromSeconds(Math.Clamp(config.RemoteWorkerPollSeconds, 1, 15));

        while (true)
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(baseUri, string.Create(CultureInfo.InvariantCulture, $"v1/jobs/{jobId}")), config);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            RemoteJobResponse job = await response.Content.ReadFromJsonAsync<RemoteJobResponse>(JsonOptions, timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Remote caption worker returned an empty job response.");

            if (string.Equals(job.State, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return job;
            }

            if (string.Equals(job.State, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(job.State, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Remote caption worker job {jobId} ended with state {job.State}: {job.Error}"));
            }

            await Task.Delay(pollDelay, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task<RemoteTranscriptResult> GetResultAsync(
        Uri baseUri,
        PluginConfiguration config,
        Guid sessionId,
        string jobId,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, new Uri(baseUri, string.Create(CultureInfo.InvariantCulture, $"v1/jobs/{jobId}/captions?format=json")), config);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RemoteTranscriptResult>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Remote caption worker job {jobId} returned an empty transcript."));
    }

    private async Task DeleteJobAsync(Uri baseUri, PluginConfiguration config, string jobId, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Delete, new Uri(baseUri, string.Create(CultureInfo.InvariantCulture, $"v1/jobs/{jobId}")), config);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auto-caption remote worker cleanup failed for job {JobId}.", jobId);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, PluginConfiguration config)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(config.RemoteWorkerApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.RemoteWorkerApiKey.Trim());
        }

        return request;
    }

    private static string GetRemoteModel(PluginConfiguration config)
    {
        return string.IsNullOrWhiteSpace(config.RemoteWorkerModel)
            ? config.PrimaryModel
            : config.RemoteWorkerModel.Trim();
    }

    private static string NormalizeLanguage(string language)
    {
        return string.IsNullOrWhiteSpace(language) || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : language.Trim();
    }

    private static void WriteVtt(string path, IReadOnlyList<RemoteSegment> segments, double offsetSeconds)
    {
        var cues = new List<RemoteCue>();
        foreach (RemoteSegment segment in segments)
        {
            string text = NormalizeCaptionText(segment.Text.Trim());
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            double rawStart = offsetSeconds + segment.Start;
            double rawEnd = offsetSeconds + segment.End;
            double start = Math.Max(0, rawStart - 0.05);
            double end = Math.Max(rawEnd + 0.5, start + 1.1);
            cues.Add(new RemoteCue(start, end, text));
        }

        for (int i = 0; i < cues.Count - 1; i++)
        {
            double maxEnd = cues[i + 1].Start - 0.02;
            if (cues[i].End > maxEnd)
            {
                cues[i] = cues[i] with { End = Math.Max(cues[i].Start + 0.2, maxEnd) };
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = new StreamWriter(path, append: false, Encoding.UTF8);
        output.WriteLine("WEBVTT");
        output.WriteLine();
        for (int i = 0; i < cues.Count; i++)
        {
            RemoteCue cue = cues[i];
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"chunk-{i}"));
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{FormatTimestamp(cue.Start)} --> {FormatTimestamp(cue.End)}"));
            output.WriteLine(cue.Text);
            output.WriteLine();
        }
    }

    private static string FormatTimestamp(double seconds)
    {
        seconds = Math.Max(0.0, seconds);
        int milliseconds = (int)Math.Floor(seconds * 1000);
        int hours = milliseconds / 3_600_000;
        milliseconds %= 3_600_000;
        int minutes = milliseconds / 60_000;
        milliseconds %= 60_000;
        int wholeSeconds = milliseconds / 1000;
        milliseconds %= 1000;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:00}:{minutes:00}:{wholeSeconds:00}.{milliseconds:000}");
    }

    private static string NormalizeCaptionText(string text)
    {
        return FirstPersonRegex.Replace(
            text,
            match =>
            {
                string suffix = match.Groups[2].Value;
                return string.IsNullOrEmpty(suffix) ? "I" : string.Create(CultureInfo.InvariantCulture, $"I'{suffix}");
            });
    }

    private sealed record RemoteJobResponse(
        [property: JsonPropertyName("job_id")] string JobId,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record RemoteTranscriptResult(
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("segments")] List<RemoteSegment> Segments);

    private sealed record RemoteSegment(
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("text")] string Text);

    private sealed record RemoteCue(double Start, double End, string Text);
}
