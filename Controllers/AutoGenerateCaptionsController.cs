using Jellyfin.Plugin.AutoGenerateCaptions.Models;
using Jellyfin.Plugin.AutoGenerateCaptions.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AutoGenerateCaptions.Controllers;

/// <summary>
/// Provides live on-demand caption generation endpoints for custom clients.
/// </summary>
[ApiController]
[Route("[controller]")]
[Authorize]
public class AutoGenerateCaptionsController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly AutoGenerateCaptionService _captionService;
    private readonly EnhancedCaptionQueueService _enhancedQueueService;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoGenerateCaptionsController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="captionService">Caption session service.</param>
    /// <param name="enhancedQueueService">Durable Enhanced caption queue.</param>
    public AutoGenerateCaptionsController(
        ILibraryManager libraryManager,
        AutoGenerateCaptionService captionService,
        EnhancedCaptionQueueService enhancedQueueService)
    {
        _libraryManager = libraryManager;
        _captionService = captionService;
        _enhancedQueueService = enhancedQueueService;
    }

    /// <summary>
    /// Starts an on-demand generated caption session for an item.
    /// </summary>
    /// <param name="itemId">Video item id.</param>
    /// <param name="request">Start request.</param>
    /// <returns>Caption session details.</returns>
    [HttpPost("Items/{itemId}/Sessions")]
    public ActionResult<CaptionSessionDto> StartSession(Guid itemId, [FromBody] StartCaptionSessionRequest request)
    {
        BaseItem? item = _libraryManager.GetItemById(itemId);
        if (item is not Video video)
        {
            return NotFound();
        }

        return _captionService.StartSession(video, request);
    }

    /// <summary>
    /// Enqueues a user-requested low-priority full-item transcription.
    /// </summary>
    /// <param name="itemId">Video item id.</param>
    /// <param name="request">Prefetch request.</param>
    /// <returns>Background caption job details.</returns>
    [HttpPost("Items/{itemId}/Prefetch")]
    [HttpPost("Items/{itemId}/Full")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<CaptionSessionDto> PrefetchItem(Guid itemId, [FromBody] PrefetchCaptionRequest request)
    {
        BaseItem? item = _libraryManager.GetItemById(itemId);
        if (item is not Video video)
        {
            return NotFound();
        }

        try
        {
            return Accepted(_captionService.StartPrefetch(video, request));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Gets Auto Generate Captions client-facing capabilities.
    /// </summary>
    /// <returns>Capability flags.</returns>
    [HttpGet("Capabilities")]
    public ActionResult<object> GetCapabilities()
    {
        return Ok(_captionService.GetCapabilities());
    }

    /// <summary>
    /// Gets active and recent caption processing jobs for the plugin administration page.
    /// </summary>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <returns>Current caption processing snapshot.</returns>
    [HttpGet("Admin/Jobs")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<CaptionProcessingSnapshotDto> GetProcessingJobs([FromQuery] int limit = 50)
    {
        return Ok(_captionService.GetProcessingSnapshot(limit));
    }

    /// <summary>
    /// Gets durable server-side Enhanced caption queue jobs.
    /// </summary>
    [HttpGet("Admin/Queue")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<CaptionQueueJobDto>> GetQueue([FromQuery] int limit = 100)
    {
        return Ok(_enhancedQueueService.GetJobs(limit));
    }

    /// <summary>
    /// Searches queueable movies, episodes, seasons, and series for the admin dashboard.
    /// </summary>
    [HttpGet("Admin/Search")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> SearchQueueableItems([FromQuery] string query, [FromQuery] int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(Array.Empty<object>());
        }

        object[] results = _libraryManager.GetItemList(new InternalItemsQuery
            {
                SearchTerm = query.Trim(),
                Recursive = true,
                Limit = Math.Clamp(limit, 1, 50)
            })
            .Where(i => i is Video || i.GetType().Name is "Series" or "Season")
            .Select(i => new
            {
                Id = i.Id,
                Name = i.Name,
                Type = i.GetType().Name,
                IsFolder = i.IsFolder
            })
            .Cast<object>()
            .ToArray();
        return Ok(results);
    }

    /// <summary>
    /// Browses the small, relevant level of the library for the Enhanced queue selector.
    /// </summary>
    [HttpGet("Admin/Browse")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<CaptionBrowseItemDto>> BrowseQueueableItems(
        [FromQuery] string kind,
        [FromQuery] Guid? parentId = null,
        [FromQuery] string? query = null,
        [FromQuery] int limit = 100)
    {
        IEnumerable<BaseItem> items;
        if (kind.Equals("series", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("movies", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Ok(Array.Empty<CaptionBrowseItemDto>());
            }

            items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                SearchTerm = query.Trim(),
                Recursive = true,
                Limit = Math.Clamp(limit, 1, 100)
            }).Where(i => kind.Equals("series", StringComparison.OrdinalIgnoreCase)
                ? i.GetType().Name == "Series"
                : i.GetType().Name == "Movie");
        }
        else if (kind.Equals("seasons", StringComparison.OrdinalIgnoreCase)
                 || kind.Equals("episodes", StringComparison.OrdinalIgnoreCase))
        {
            if (!parentId.HasValue || _libraryManager.GetItemById(parentId.Value) is null)
            {
                return NotFound();
            }

            string expectedType = kind.Equals("seasons", StringComparison.OrdinalIgnoreCase) ? "Season" : "Episode";
            items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = parentId.Value,
                Recursive = false,
                Limit = Math.Clamp(limit, 1, 500)
            }).Where(i => i.GetType().Name == expectedType);
        }
        else
        {
            return BadRequest(new { Error = "kind must be series, movies, seasons, or episodes." });
        }

        IReadOnlySet<Guid> activeQueueItemIds = _enhancedQueueService.GetActiveItemIds();
        return Ok(items
            .OrderBy(i => i.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(i => i.IndexNumber ?? int.MaxValue)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new CaptionBrowseItemDto
            {
                Id = i.Id,
                Name = i.Name ?? string.Empty,
                Type = i.GetType().Name,
                ParentIndexNumber = i.ParentIndexNumber,
                IndexNumber = i.IndexNumber,
                HasVttSidecar = HasVttSidecar(i),
                IsQueued = activeQueueItemIds.Contains(i.Id)
            })
            .ToArray());
    }

    private static bool HasVttSidecar(BaseItem item)
    {
        if (item is not Video video || string.IsNullOrWhiteSpace(video.Path))
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(video.Path);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(video.Path);
        if (string.IsNullOrWhiteSpace(directory)
            || string.IsNullOrWhiteSpace(fileNameWithoutExtension)
            || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(directory, fileNameWithoutExtension + "*")
                .Any(path => Path.GetExtension(path).Equals(".vtt", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Queues a movie, episode, season, or series for server-side Enhanced caption generation.
    /// </summary>
    [HttpPost("Admin/Queue/{itemId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<CaptionQueueJobDto>> EnqueueEnhanced(Guid itemId, [FromQuery] bool overwriteExistingSubtitle = false)
    {
        BaseItem? item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        IReadOnlyList<CaptionQueueJobDto> jobs = _enhancedQueueService.Enqueue(item, overwriteExistingSubtitle);
        return Accepted(jobs);
    }

    /// <summary>
    /// Re-runs the configured full-caption polish on an item's cached captions without retranscribing it.
    /// </summary>
    /// <param name="itemId">Video item id.</param>
    /// <returns>Background caption session details.</returns>
    [HttpPost("Admin/Items/{itemId}/Polish")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<CaptionSessionDto> PolishCachedItem(Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not Video video)
        {
            return NotFound();
        }

        try
        {
            return Accepted(_captionService.StartCachedPolish(video));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Clears waiting jobs or terminal queue history.
    /// </summary>
    [HttpPost("Admin/Queue/Clear")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> ClearEnhancedQueue([FromQuery] string scope = "waiting")
    {
        int removed = scope.Equals("history", StringComparison.OrdinalIgnoreCase)
            ? _enhancedQueueService.ClearHistory()
            : _enhancedQueueService.ClearWaiting();
        return Ok(new { Removed = removed, Scope = scope });
    }

    /// <summary>
    /// Retries a terminal Enhanced queue job.
    /// </summary>
    [HttpPost("Admin/Queue/{jobId}/Retry")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> RetryEnhancedJob(Guid jobId)
    {
        return _enhancedQueueService.Retry(jobId) ? Ok(new { Retried = jobId }) : NotFound();
    }

    /// <summary>
    /// Removes a waiting Enhanced job or cancels an active one.
    /// </summary>
    [HttpPost("Admin/Queue/{jobId}/Cancel")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> CancelEnhancedJob(Guid jobId)
    {
        return _enhancedQueueService.Cancel(jobId) ? Ok(new { Cancelled = jobId }) : NotFound();
    }

    /// <summary>
    /// Clears generated caption cache entries for an item.
    /// </summary>
    /// <param name="itemId">Video item id.</param>
    /// <param name="mediaSourceId">Optional media source id to match older untagged cache entries.</param>
    /// <param name="audioStreamIndex">Optional audio stream index to match older untagged cache entries.</param>
    /// <param name="language">Optional generated caption language to match older untagged cache entries.</param>
    /// <returns>Clear result.</returns>
    [HttpPost("Items/{itemId}/Cache/Clear")]
    public ActionResult<object> ClearItemCache(
        Guid itemId,
        [FromQuery] string? mediaSourceId,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] string? language)
    {
        BaseItem? item = _libraryManager.GetItemById(itemId);
        if (item is not Video video)
        {
            return NotFound();
        }

        int deletedDirectories = _captionService.ClearItemCache(video, mediaSourceId, audioStreamIndex, language);
        return Ok(new
        {
            ItemId = itemId,
            DeletedDirectories = deletedDirectories
        });
    }

    /// <summary>
    /// Gets a caption session status.
    /// </summary>
    /// <param name="sessionId">Caption session id.</param>
    /// <returns>Session status.</returns>
    [HttpGet("Sessions/{sessionId}")]
    public ActionResult<CaptionSessionStatusDto> GetSession(Guid sessionId)
    {
        CaptionSessionStatusDto? status = _captionService.GetStatus(sessionId);
        return status is null ? NotFound() : status;
    }

    /// <summary>
    /// Stops a caption session.
    /// </summary>
    /// <param name="sessionId">Caption session id.</param>
    /// <returns>Stopped session status.</returns>
    [HttpPost("Sessions/{sessionId}/Stop")]
    public ActionResult<CaptionSessionStatusDto> StopSession(Guid sessionId)
    {
        CaptionSessionStatusDto? status = _captionService.StopSession(sessionId);
        return status is null ? NotFound() : status;
    }

    /// <summary>
    /// Gets the current live WebVTT content for a caption session.
    /// </summary>
    /// <param name="sessionId">Caption session id.</param>
    /// <param name="positionTicks">Current client playback position in ticks.</param>
    /// <returns>WebVTT payload.</returns>
    [HttpGet("{sessionId}/live.vtt")]
    [Produces("text/vtt")]
    public ActionResult GetLiveVtt(Guid sessionId, [FromQuery] long? positionTicks)
    {
        string? vtt = _captionService.GetLiveVtt(sessionId, positionTicks);
        return vtt is null ? NotFound() : Content(vtt, "text/vtt; charset=utf-8");
    }
}
