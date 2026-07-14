namespace Jellyfin.Plugin.AutoGenerateCaptions.Models;

/// <summary>
/// Snapshot returned to the administrative processing view.
/// </summary>
public class CaptionProcessingSnapshotDto
{
    /// <summary>
    /// Gets or sets when the snapshot was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>
    /// Gets or sets the number of active plugin sessions.
    /// </summary>
    public int ActiveJobs { get; set; }

    /// <summary>
    /// Gets or sets the number of jobs currently queued by the remote worker.
    /// </summary>
    public int QueuedWorkerJobs { get; set; }

    /// <summary>
    /// Gets or sets the number of jobs currently running on the remote worker.
    /// </summary>
    public int RunningWorkerJobs { get; set; }

    /// <summary>
    /// Gets or sets active and recent jobs, with active jobs first.
    /// </summary>
    public IReadOnlyList<CaptionProcessingJobDto> Jobs { get; set; } = [];
}
