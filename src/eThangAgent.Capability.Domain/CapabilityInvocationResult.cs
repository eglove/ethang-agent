using eThangAgent.ToolDomain;

namespace eThangAgent.CapabilityDomain;

/// <summary>The outcome of one capability dispatch: the text, the error flag, and the
///     IMAGES the action attached (issue #288). Images ride the result because the single
///     door cannot drop them: a nested Tools.read_image, Tools.docx extract, Tools.pdf
///     render or Tools.computer screenshot must reach the model exactly as it did when
///     those tools were first-class loop tools. A provider that produces no images leaves
///     the list empty — never null, so the exec engine can concatenate without a guard.
///     The domain type stays Tool Domain's <c>ToolResultImage</c> (the shape every image
///     carrier already agrees on) rather than a parallel record.</summary>
public sealed record CapabilityInvocationResult(string Content, bool IsError,
    IReadOnlyList<ToolResultImage>? Images = null)
{
  /// <summary>The attached images, never null (absent reads as empty).</summary>
  public IReadOnlyList<ToolResultImage> Images { get; init; } = Images ?? [];

  public static CapabilityInvocationResult Ok(string content) => new(content, false);

  public static CapabilityInvocationResult Ok(string content, IReadOnlyList<ToolResultImage>? images)
      => new(content, false, images);

  public static CapabilityInvocationResult Fail(string content) => new(content, true);

  public static CapabilityInvocationResult Fail(string content, IReadOnlyList<ToolResultImage>? images)
      => new(content, true, images);
}
