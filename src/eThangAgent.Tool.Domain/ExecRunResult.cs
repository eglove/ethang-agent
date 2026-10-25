namespace eThangAgent.ToolDomain;

public sealed record ExecRunResult(
    ExecRunStatus Status,
    string Output,
    IReadOnlyList<string> ErrorLines,
    string? ErrorMessage = null,
    int NestedDispatchCount = 0,
    string? WorkspaceRoot = null,
    string? LaunchDirectory = null,
    WorkspaceChangeSet? Changes = null,
    IReadOnlyList<ToolResultImage>? Images = null,
    int DroppedImageCount = 0)
{
  /// <summary>The images the run's nested tool calls attached (issue #288): every
  ///     Tools.read_image / docx extract / pdf render / computer screenshot dispatch
  ///     contributes its <c>ToolResult.Images</c> here, in dispatch order, bounded by
  ///     <see cref="ImageLimits.MaxPerMessage"/>. Never null — a run that dispatched
  ///     nothing image-bearing carries an empty list. This is the channel that lets exec
  ///     be the ONLY top-level tool without losing the image surface.</summary>
  public IReadOnlyList<ToolResultImage> Images { get; init; } = Images ?? [];

  public static ExecRunResult Completed(string output, int nestedDispatchCount = 0)
      => new(ExecRunStatus.Completed, output, [], NestedDispatchCount: nestedDispatchCount);
}
