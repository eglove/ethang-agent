using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The read_image tool (issue #20): reads one image file from the workspace
///     and attaches it to the tool result - riding the existing tool-result image
///     pipeline (ToolResult.Images -> Message.Parts -> provider wire). The format is
///     sniffed from magic bytes (image/png or image/jpeg only - the media types every
///     image carrier enforces); anything else is a typed rejection. The 20 MB bound
///     fires after the read. Vision gating mirrors the computer tool's screenshot
///     contract: without image input the read still succeeds textually (format and
///     size reported) and the image is withheld behind a notice line, so a text-only
///     model learns to stop attaching instead of silently losing bytes.</summary>
public sealed class ReadImageTool(IPathResolver resolver, IFileSystemAccess files, IImageInputCapability vision)
    : ITool, IWorkspaceScopedTool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IFileSystemAccess _files = files ?? throw new ArgumentNullException(nameof(files));
  private readonly IImageInputCapability _vision = vision ?? throw new ArgumentNullException(nameof(vision));

  /// <inheritdoc />
  public ITool RootedAt(string workspaceRoot)
      => new ReadImageTool(new WorkspacePathResolver(workspaceRoot), _files, _vision);

  public ToolDefinition Definition { get; } = new(
      "read_image",
      "Read one image file from the workspace and attach it to this result so you can see it. "
      + "timeoutSeconds and path are mandatory. Formats: exactly image/png or image/jpeg "
      + "(sniffed from file bytes; other formats fail with UnsupportedImageFormat). "
      + "Maximum size: 20 MB decoded (larger files fail with ImageTooLarge). "
      + "Output begins with one annotation line in [brackets] - metadata, not file content: "
      + "[read_image <resolved path> <media type>, N bytes]. When the model has no image input "
      + "the image is withheld and the line `[read_image] image withheld: model has no image input` "
      + "is appended - do not re-read; the attachment cannot succeed until the model changes.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("path", ToolParameterType.Text,
              "Image file path, workspace-relative or absolute-inside-workspace."),
      ],
      [ToolTimeout.ParameterName, "path"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ReadImageToolInput> parsed = ReadImageToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<string> resolved = _resolver.Resolve(parsed.Value.Path);
    if (!resolved.IsSuccess)
    {
      return Task.FromResult(Err(resolved.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => ReadAsync(resolved.Value, token), ct);
  }

  private async Task<ToolResult> ReadAsync(string path, CancellationToken ct)
  {
    Result<byte[]> read = await _files.ReadBytesAsync(path, ct).ConfigureAwait(false);
    if (!read.IsSuccess)
    {
      return Err(read.Error);
    }

    byte[] bytes = read.Value;
    string? mediaType = ImageFormats.SniffMediaType(bytes);
    if (mediaType is null)
    {
      return Err(new DomainError("UnsupportedImageFormat",
          $"'{path}' is not an image this tool can attach. Formats: image/png or image/jpeg "
          + "(sniffed from file bytes). Convert the file first, then retry."));
    }

    if (bytes.Length > ImageLimits.MaxBytes)
    {
      return Err(new DomainError("ImageTooLarge",
          $"'{path}' is {bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes decoded; "
          + $"the maximum is {ImageLimits.MaxBytes.ToString(CultureInfo.InvariantCulture)} bytes (20 MB)."));
    }

    string annotation = $"[read_image {path} {mediaType}, {bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes]";
    if (!_vision.AcceptsImages)
    {
      return new ToolResult(annotation + "\n[read_image] image withheld: model has no image input", false);
    }

    ToolResultImage image = new(mediaType, Convert.ToBase64String(bytes));
    return new ToolResult(annotation, false, Images: [image]);
  }

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
