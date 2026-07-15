namespace Jellyfin.Plugin.AutoGenerateCaptions.Models;

/// <summary>
/// A queueable item in the server-side media selector.
/// </summary>
public class CaptionBrowseItemDto
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional season number.</summary>
    public int? ParentIndexNumber { get; set; }

    /// <summary>Gets or sets the optional episode number.</summary>
    public int? IndexNumber { get; set; }
}
