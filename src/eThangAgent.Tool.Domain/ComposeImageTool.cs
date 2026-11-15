using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The compose_image tool: the harness's own drawing surface.
///     <para>NO MODEL, NO PROVIDER, NO NETWORK. A caller describes a picture as a scene - a canvas
///     size, an optional background, and a list of shapes - and gets a finished PNG file back at
///     an absolute path. Cost is a few milliseconds of local rasterization, so a featured image,
///     a diagram, a chart, or a card is something a session can produce on ANY provider, with ANY
///     model, including a text-only one, and can reproduce exactly by re-running the same
///     document.</para>
///     <para>THE SCENE IS DATA, NOT CODE. The vocabulary is deliberately small and declarative;
///     anything the primitives cannot express should be a new primitive with a rule of its own,
///     never a caller's script. The renderer stays replaceable behind
///     <see cref="IImageCompositionAccess"/>.</para>
///     <para>THE ANSWER IS THE FILE, and the picture additionally rides the tool-result channel
///     when the session's model accepts image input - the read_image contract, reused, so a
///     model that CAN see gets the picture and one that cannot is told plainly rather than left
///     retrying.</para></summary>
public sealed class ComposeImageTool(IImageCompositionAccess renderer, IImageStore images,
    IImageInputCapability vision) : ITool
{
  private readonly IImageCompositionAccess _renderer =
      renderer ?? throw new ArgumentNullException(nameof(renderer));
  private readonly IImageStore _images = images ?? throw new ArgumentNullException(nameof(images));
  private readonly IImageInputCapability _vision = vision ?? throw new ArgumentNullException(nameof(vision));

  /// <inheritdoc />
  public ToolDefinition Definition { get; } = new(
      "compose_image",
      BuildDescription(),
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber,
              ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("width", ToolParameterType.WholeNumber,
              "Required. Canvas width in pixels, 1 to 4096."),
          new ToolParameter("height", ToolParameterType.WholeNumber,
              "Required. Canvas height in pixels, 1 to 4096."),
          new ToolParameter("background", ToolParameterType.Text,
              "Optional. Canvas colour as #RRGGBB or #RRGGBBAA. Omit for a transparent canvas."),
          new ToolParameter("shapes", ToolParameterType.ObjectArray,
              "The shapes in draw order (later shapes paint over earlier ones). Each is an object with a kind: rectangle, rounded_rectangle, ellipse, line, polyline, or text."),
      ],
      [ToolTimeout.ParameterName, "width", "height"]);

  /// <summary>The description IS the contract: the primitive vocabulary, every key each shape
  ///     accepts, the output shape, and every error code appear verbatim so the model composes a
  ///     document it can predict rather than discovering the rules by rejection.</summary>
  private static string BuildDescription()
  {
    return "Draw an image from a list of shapes and save it as a PNG file. Local rasterization:"
     +
        " no model is called and no network is used, so this works on any provider and any model."
        + " width and height and timeoutSeconds are mandatory; background and shapes are optional."
        + " Colours are #RRGGBB or #RRGGBBAA. Shapes are drawn in array order: later shapes paint over"
        + " earlier ones."
        + " Kinds and their keys:rectangle = x, y, width, height.rounded_rectangle = x, y, width,"
        + " height, corner_radius (default 0).ellipse = x, y, width, height (the bounding box)."
        + " line = from {x, y}, to {x, y}.polyline = points [{x, y}, ...] with at least 2 points, plus"
        + " optional closed (default false).text = text, x, y (TOP-LEFT of the run), font_size,"
        + " optional font_family, and color."
        + " Paint keys on every kind except line and text: fill, and optionally stroke_width with"
        + " stroke_color (both or neither). A shape with no fill and no stroke draws nothing, so it is"
        + " refused. A fill on a polyline needs closed = true. line requires stroke."
        + " Output begins with one annotation line in [brackets] - metadata, not file content:"
        + " [compose_image <absolute path> image/png, WxH, N bytes]. The path is where the PNG was"
        + " saved; hand it to read_image to look at it again, or to a file tool to move it into the"
        + " workspace."
        + " When the session's model has no image input the picture is withheld and the line"
        + " '[compose_image] image withheld: model has no image input' is appended - the file still"
        + " exists at the named path."
        + " Failures render 'Error [Code]: <message>'. Error codes: InvalidParameterType,"
        + " InvalidParameterValue, MissingParameter, UnknownParameter (the scene or an argument was"
        + " wrong - the message names the shape index), ImageCompositionFailed (this machine could not"
        + " rasterize, e.g. no typeface for text), ImageUnwritable (the PNG could not be saved)."
        + " Errors are safe to retry with a corrected scene.";
  }

  /// <inheritdoc />
  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ComposeImageToolInput> parsed = ComposeImageToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout,
          token => RunAsync(parsed.Value.Scene, token), ct);
  }

  private async Task<ToolResult> RunAsync(SceneImage scene, CancellationToken ct)
  {
    Result<ComposedImage> rendered = await _renderer.RenderAsync(scene, ct).ConfigureAwait(false);
    if (!rendered.IsSuccess)
    {
      return Err(rendered.Error);
    }

    ComposedImage image = rendered.Value;
    Result<StoredImage> stored = await _images
        .PersistAsync(ImagePayload.Png, image.PngBytes, ct).ConfigureAwait(false);
    if (!stored.IsSuccess)
    {
      return Err(stored.Error);
    }

    StoredImage saved = stored.Value;
    string annotation = $"[compose_image {saved.Path} {saved.MediaType}, "
        + $"{image.Width.ToString(CultureInfo.InvariantCulture)}x{image.Height.ToString(CultureInfo.InvariantCulture)}, "
        + $"{image.PngBytes.Length.ToString(CultureInfo.InvariantCulture)} bytes]";

    return !_vision.AcceptsImages
        ? new ToolResult(annotation
            + "\n[compose_image] image withheld: model has no image input", false)
        : new ToolResult(annotation, false,
            Images: [new ToolResultImage(saved.MediaType, Convert.ToBase64String(image.PngBytes))]);
  }

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
