using Jellyfin.Plugin.AutoGenerateCaptions.Models;
using Jellyfin.Plugin.AutoGenerateCaptions.Services;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoGenerateCaptionsController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="captionService">Caption session service.</param>
    public AutoGenerateCaptionsController(
        ILibraryManager libraryManager,
        AutoGenerateCaptionService captionService)
    {
        _libraryManager = libraryManager;
        _captionService = captionService;
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
