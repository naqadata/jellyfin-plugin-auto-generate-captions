namespace Jellyfin.Plugin.AutoGenerateCaptions.Models;

/// <summary>
/// Describes a generated caption range in the cache.
/// </summary>
public class CaptionCacheRangeDto
{
    /// <summary>
    /// Gets or sets the start position in ticks.
    /// </summary>
    public long StartTicks { get; set; }

    /// <summary>
    /// Gets or sets the end position in ticks.
    /// </summary>
    public long EndTicks { get; set; }
}
