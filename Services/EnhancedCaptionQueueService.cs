using System.Text.Json;
using Jellyfin.Plugin.AutoGenerateCaptions.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoGenerateCaptions.Services;

/// <summary>
/// Persists and executes one server-owned Enhanced caption job at a time.
/// </summary>
public class EnhancedCaptionQueueService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _syncRoot = new();
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly AutoGenerateCaptionService _captionService;
    private readonly ILogger<EnhancedCaptionQueueService> _logger;
    private List<CaptionQueueJobDto> _jobs = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="EnhancedCaptionQueueService"/> class.
    /// </summary>
    public EnhancedCaptionQueueService(
        IApplicationPaths applicationPaths,
        ILibraryManager libraryManager,
        AutoGenerateCaptionService captionService,
        ILogger<EnhancedCaptionQueueService> logger)
    {
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
        _captionService = captionService;
        _logger = logger;
        Load();
    }

    /// <summary>
    /// Gets the durable queue in execution order, followed by recent terminal history.
    /// </summary>
    public IReadOnlyList<CaptionQueueJobDto> GetJobs(int limit = 100)
    {
        lock (_syncRoot)
        {
            return _jobs
                .Where(i => i.Status is "running" or "polishing" or "pending-polish" or "queued")
                .OrderBy(i => i.Status == "running" ? 0 : i.Status == "polishing" ? 1 : i.Status == "pending-polish" ? 2 : 3)
                .ThenBy(i => i.CreatedAt)
                .Concat(_jobs
                    .Where(i => i.Status is not "running" and not "polishing" and not "queued")
                    .OrderByDescending(i => i.CompletedAt ?? i.CreatedAt))
                .Take(Math.Clamp(limit, 1, 250))
                .Select(Clone)
                .ToArray();
        }
    }

    /// <summary>
    /// Gets item ids that currently have non-terminal Enhanced queue work.
    /// </summary>
    public IReadOnlySet<Guid> GetActiveItemIds()
    {
        lock (_syncRoot)
        {
            return _jobs
                .Where(i => i.Status is "queued" or "running" or "polishing" or "pending-polish")
                .Select(i => i.ItemId)
                .ToHashSet();
        }
    }

    /// <summary>
    /// Expands a movie, episode, season, or series into durable video jobs.
    /// </summary>
    public IReadOnlyList<CaptionQueueJobDto> Enqueue(BaseItem selected, bool overwriteExistingSubtitle)
    {
        ArgumentNullException.ThrowIfNull(selected);
        Video[] videos = selected is Video video
            ? [video]
            : _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = selected.Id,
                Recursive = true
            }).OfType<Video>()
                .Where(i => !string.IsNullOrWhiteSpace(i.Path))
                .OrderBy(i => i.ParentIndexNumber ?? int.MaxValue)
                .ThenBy(i => i.IndexNumber ?? int.MaxValue)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        List<CaptionQueueJobDto> added = [];
        lock (_syncRoot)
        {
            foreach (Video item in videos)
            {
                if (_jobs.Any(i => i.ItemId == item.Id && i.Status is "queued" or "running" or "polishing" or "pending-polish"))
                {
                    continue;
                }

                var job = new CaptionQueueJobDto
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    ItemName = GetDisplayName(item),
                    OverwriteExistingSubtitle = overwriteExistingSubtitle,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Message = "Queued for server-side Enhanced caption generation."
                };
                _jobs.Add(job);
                added.Add(Clone(job));
            }

            SaveLocked();
        }

        _logger.LogInformation("Queued {Count} Enhanced caption job(s) from {SelectedItem}", added.Count, selected.Name);
        return added;
    }

    /// <summary>
    /// Removes waiting jobs without interrupting active transcription.
    /// </summary>
    /// <returns>The number of removed jobs.</returns>
    public int ClearWaiting()
    {
        lock (_syncRoot)
        {
            int removed = _jobs.RemoveAll(i => i.Status == "queued");
            SaveLocked();
            return removed;
        }
    }

    /// <summary>
    /// Removes terminal job history.
    /// </summary>
    /// <returns>The number of removed jobs.</returns>
    public int ClearHistory()
    {
        lock (_syncRoot)
        {
            int removed = _jobs.RemoveAll(i => i.Status is "complete" or "cached" or "failed" or "stopped" or "skipped");
            SaveLocked();
            return removed;
        }
    }

    /// <summary>
    /// Retries a terminal job by returning it to the end of the queue.
    /// </summary>
    /// <param name="jobId">Durable queue job id.</param>
    /// <returns>Whether the job was retried.</returns>
    public bool Retry(Guid jobId)
    {
        lock (_syncRoot)
        {
            CaptionQueueJobDto? job = _jobs.FirstOrDefault(i => i.Id == jobId);
            if (job is null || job.Status is "queued" or "running" or "polishing" or "pending-polish")
            {
                return false;
            }

            job.Status = "queued";
            job.SessionId = null;
            job.StartedAt = null;
            job.CompletedAt = null;
            job.ProgressPercent = 0;
            job.ProcessingPhase = null;
            job.CreatedAt = DateTimeOffset.UtcNow;
            job.Message = "Retry requested from the server queue.";
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// Removes a waiting job or cancels an active background job.
    /// </summary>
    /// <param name="jobId">Durable queue job id.</param>
    /// <returns>Whether a job was removed or cancelled.</returns>
    public bool Cancel(Guid jobId)
    {
        Guid? sessionId = null;
        lock (_syncRoot)
        {
            CaptionQueueJobDto? job = _jobs.FirstOrDefault(i => i.Id == jobId);
            if (job is null)
            {
                return false;
            }

            if (job.Status == "queued")
            {
                _jobs.Remove(job);
                SaveLocked();
                return true;
            }

            if (job.Status == "pending-polish")
            {
                job.Status = "stopped";
                job.Message = "Deferred polish cancelled from the server queue.";
                job.CompletedAt = DateTimeOffset.UtcNow;
                SaveLocked();
                return true;
            }

            if (job.Status is not "running" and not "polishing")
            {
                return false;
            }

            sessionId = job.SessionId;
            job.Status = "stopped";
            job.Message = "Cancelled from the server queue.";
            job.CompletedAt = DateTimeOffset.UtcNow;
            SaveLocked();
        }

        if (sessionId.HasValue)
        {
            _captionService.StopBackgroundSession(sessionId.Value);
        }

        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        ReapplyKnownEnhancedSubtitleTitles();

        while (!stoppingToken.IsCancellationRequested)
        {
            CaptionQueueJobDto? next;
            CaptionQueueJobDto? nextPolish = null;
            lock (_syncRoot)
            {
                next = _jobs.FirstOrDefault(i => i.Status == "queued");
                if (next is not null)
                {
                    next.Status = "running";
                    next.StartedAt = DateTimeOffset.UtcNow;
                    next.Message = "Starting server-side Enhanced generation.";
                    SaveLocked();
                }
                else
                {
                    nextPolish = _jobs.FirstOrDefault(i => i.Status == "pending-polish");
                    if (nextPolish is not null)
                    {
                        nextPolish.Status = "polishing";
                        nextPolish.StartedAt ??= DateTimeOffset.UtcNow;
                        nextPolish.Message = "Starting deferred local caption polish after transcription queue drained.";
                        SaveLocked();
                    }
                }
            }

            if (next is not null)
            {
                await ProcessAsync(next, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (nextPolish is not null)
            {
                await ProcessPolishAsync(nextPolish, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
    }

    private void ReapplyKnownEnhancedSubtitleTitles()
    {
        Guid[] itemIds;
        lock (_syncRoot)
        {
            itemIds = _jobs
                .Where(i => i.Status is "complete" or "cached" or "skipped")
                .Select(i => i.ItemId)
                .Distinct()
                .ToArray();
        }

        int relabeled = 0;
        foreach (Guid itemId in itemIds)
        {
            if (_libraryManager.GetItemById(itemId) is Video video
                && _captionService.TryReapplyEnhancedSubtitleTitle(video))
            {
                relabeled++;
            }
        }

        _logger.LogInformation("Reapplied Enhanced subtitle labels for {Count} known sidecar(s)", relabeled);
    }

    private async Task ProcessAsync(CaptionQueueJobDto job, CancellationToken stoppingToken)
    {
        try
        {
            if (_libraryManager.GetItemById(job.ItemId) is not Video video)
            {
                Complete(job.Id, "failed", "The queued library item is no longer a video item.");
                return;
            }

            CaptionSessionDto session = _captionService.StartPrefetch(video, new PrefetchCaptionRequest
            {
                OverwriteExistingSubtitle = job.OverwriteExistingSubtitle,
                DeferCaptionPolish = true
            });
            lock (_syncRoot)
            {
                CaptionQueueJobDto? current = _jobs.FirstOrDefault(i => i.Id == job.Id);
                if (current is not null)
                {
                    current.SessionId = session.SessionId;
                    current.ProgressPercent = session.ProgressPercent;
                    current.ProcessingPhase = session.ProcessingPhase;
                    current.Message = "Queued on the caption worker.";
                    SaveLocked();
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                CaptionSessionStatusDto? status = _captionService.GetStatus(session.SessionId);
                if (status is null)
                {
                    Complete(job.Id, "failed", "Caption session disappeared before it completed.");
                    return;
                }

                if (status.Status is CaptionSessionStatuses.Complete or CaptionSessionStatuses.Cached)
                {
                    if (string.Equals(status.ProcessingPhase, "awaiting-polish", StringComparison.OrdinalIgnoreCase))
                    {
                        MarkPendingPolish(job.Id, status);
                        return;
                    }

                    Complete(job.Id, "complete", status.Message ?? "Enhanced subtitle written beside media.");
                    return;
                }

                if (status.Status is CaptionSessionStatuses.Failed or CaptionSessionStatuses.Stopped or CaptionSessionStatuses.Skipped)
                {
                    Complete(job.Id, status.Status, status.Message ?? "Enhanced caption generation did not complete.");
                    return;
                }

                lock (_syncRoot)
                {
                    CaptionQueueJobDto? current = _jobs.FirstOrDefault(i => i.Id == job.Id);
                    if (current is not null)
                    {
                        current.ProgressPercent = status.ProgressPercent;
                        current.ProcessingPhase = status.ProcessingPhase;
                        current.Message = status.Message;
                        SaveLocked();
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Requeue(job.Id, "Requeued because Jellyfin is restarting.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server-side Enhanced caption job {JobId} failed for {ItemName}", job.Id, job.ItemName);
            Complete(job.Id, "failed", ex.Message);
        }
    }

    private async Task ProcessPolishAsync(CaptionQueueJobDto job, CancellationToken stoppingToken)
    {
        try
        {
            if (_libraryManager.GetItemById(job.ItemId) is not Video video)
            {
                Complete(job.Id, "failed", "The queued library item is no longer a video item.");
                return;
            }

            CaptionSessionDto session = _captionService.StartCachedPolish(video);
            lock (_syncRoot)
            {
                CaptionQueueJobDto? current = _jobs.FirstOrDefault(i => i.Id == job.Id);
                if (current is not null)
                {
                    current.SessionId = session.SessionId;
                    current.ProcessingPhase = "polishing";
                    current.Message = "Polishing cached captions locally.";
                    SaveLocked();
                }
            }

            await MonitorPolishAsync(job.Id, session.SessionId, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            MarkPendingPolish(job.Id, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deferred caption polish job {JobId} failed for {ItemName}", job.Id, job.ItemName);
            Complete(job.Id, "failed", ex.Message);
        }
    }

    private async Task MonitorPolishAsync(Guid jobId, Guid sessionId, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                CaptionSessionStatusDto? status = _captionService.GetStatus(sessionId);
                if (status is null)
                {
                    Complete(jobId, "failed", "Caption polish session disappeared before it completed.");
                    return;
                }

                if (status.Status is CaptionSessionStatuses.Complete or CaptionSessionStatuses.Cached)
                {
                    Complete(jobId, "complete", status.Message ?? "Enhanced subtitle written beside media.");
                    return;
                }

                if (status.Status is CaptionSessionStatuses.Failed or CaptionSessionStatuses.Stopped or CaptionSessionStatuses.Skipped)
                {
                    Complete(jobId, status.Status, status.Message ?? "Enhanced caption polishing did not complete.");
                    return;
                }

                MarkPolishing(jobId, status);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            MarkPendingPolish(jobId, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server-side Enhanced caption polish monitor failed for job {JobId}", jobId);
            Complete(jobId, "failed", ex.Message);
        }
    }

    private void MarkPolishing(Guid jobId, CaptionSessionStatusDto status)
    {
        lock (_syncRoot)
        {
            CaptionQueueJobDto? job = _jobs.FirstOrDefault(i => i.Id == jobId);
            if (job is null || job.Status == "stopped")
            {
                return;
            }

            job.Status = "polishing";
            job.ProgressPercent = status.ProgressPercent;
            job.ProcessingPhase = "polishing";
            job.Message = status.Message ?? "Polishing captions.";
            SaveLocked();
        }
    }

    private void MarkPendingPolish(Guid jobId, CaptionSessionStatusDto? status)
    {
        lock (_syncRoot)
        {
            CaptionQueueJobDto? job = _jobs.FirstOrDefault(i => i.Id == jobId);
            if (job is null || job.Status == "stopped")
            {
                return;
            }

            job.Status = "pending-polish";
            job.SessionId = null;
            job.ProgressPercent = status?.ProgressPercent ?? 100;
            job.ProcessingPhase = "awaiting-polish";
            job.Message = status?.Message ?? "Raw diarized captions are ready; waiting for the transcription queue to drain before polishing.";
            SaveLocked();
        }
    }

    private void Complete(Guid jobId, string status, string message)
    {
        lock (_syncRoot)
        {
            CaptionQueueJobDto? job = _jobs.FirstOrDefault(i => i.Id == jobId);
            if (job is null)
            {
                return;
            }

            job.Status = status;
            job.ProcessingPhase = status;
            if (status is "complete" or "cached")
            {
                job.ProgressPercent = 100;
            }
            job.Message = message;
            job.CompletedAt = DateTimeOffset.UtcNow;
            SaveLocked();
        }
    }

    private void Requeue(Guid jobId, string message)
    {
        lock (_syncRoot)
        {
            CaptionQueueJobDto? job = _jobs.FirstOrDefault(i => i.Id == jobId);
            if (job is null)
            {
                return;
            }

            job.Status = "queued";
            job.SessionId = null;
            job.StartedAt = null;
            job.CompletedAt = null;
            job.ProgressPercent = 0;
            job.ProcessingPhase = null;
            job.Message = message;
            SaveLocked();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(QueuePath))
            {
                return;
            }

            _jobs = JsonSerializer.Deserialize<List<CaptionQueueJobDto>>(File.ReadAllText(QueuePath), JsonOptions) ?? [];
            foreach (CaptionQueueJobDto job in _jobs.Where(i => i.Status == "failed"
                         && i.Message?.StartsWith("Refusing to replace existing subtitle sidecar", StringComparison.Ordinal) == true))
            {
                job.Status = "skipped";
                job.ProcessingPhase = "skipped";
            }
            foreach (CaptionQueueJobDto job in _jobs.Where(i => i.Status == "running"))
            {
                job.Status = "queued";
                job.SessionId = null;
                job.StartedAt = null;
                job.Message = "Requeued after Jellyfin restarted.";
            }
            foreach (CaptionQueueJobDto job in _jobs.Where(i => i.Status == "polishing"))
            {
                job.Status = "pending-polish";
                job.SessionId = null;
                job.Message = "Deferred polish is waiting to resume after Jellyfin restarted.";
            }

            SaveLocked();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the durable Enhanced caption queue");
            _jobs = [];
        }
    }

    private string QueuePath => Path.Combine(_applicationPaths.DataPath, "auto-generate-captions", "enhanced-queue.json");

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
        string temporaryPath = QueuePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_jobs, JsonOptions));
        File.Move(temporaryPath, QueuePath, overwrite: true);
    }

    private static CaptionQueueJobDto Clone(CaptionQueueJobDto job) => new()
    {
        Id = job.Id,
        ItemId = job.ItemId,
        ItemName = job.ItemName,
        Status = job.Status,
        OverwriteExistingSubtitle = job.OverwriteExistingSubtitle,
        SessionId = job.SessionId,
        Message = job.Message,
        ProgressPercent = job.ProgressPercent,
        ProcessingPhase = job.ProcessingPhase,
        CreatedAt = job.CreatedAt,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt
    };

    private static string GetDisplayName(Video video)
    {
        if (video.ParentIndexNumber.HasValue && video.IndexNumber.HasValue)
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"S{video.ParentIndexNumber:00}E{video.IndexNumber:00} - {video.Name ?? video.Id.ToString("N")}");
        }

        return video.Name ?? video.Id.ToString("N");
    }
}
