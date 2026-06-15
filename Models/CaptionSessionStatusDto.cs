namespace Jellyfin.Plugin.AutoGenerateCaptions.Models;

/// <summary>
/// Detailed status for an auto-generated caption session.
/// </summary>
public class CaptionSessionStatusDto : CaptionSessionDto
{
    /// <summary>
    /// Gets or sets generated cache ranges.
    /// </summary>
    public IReadOnlyList<CaptionCacheRangeDto> Ranges { get; set; } = [];

    /// <summary>
    /// Gets or sets the last status message.
    /// </summary>
    public string? Message { get; set; }
}
