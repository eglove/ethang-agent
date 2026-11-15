using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>A rendered image: PNG bytes and the pixel size they encode, so a caller can report
///     the size it actually got without decoding the file itself.
///     <para>PNG IS THE ONLY FORMAT. A composed image is a diagram or a graphic, so a lossless
///     encoding is the only one that cannot introduce artifacts around text and thin strokes;
///     an ACL that offered another format would be offering quality loss nobody asked for.</para></summary>
/// <param name="PngBytes">The encoded PNG bytes, non-empty.</param>
/// <param name="Width">The rendered width in pixels.</param>
/// <param name="Height">The rendered height in pixels.</param>
// CA1819: the positional byte[] member IS the payload (the same shape ToolResultImage and
// StoredImageContent use); a copy-per-read would duplicate a multi-megabyte image.
#pragma warning disable CA1819
public sealed record ComposedImage(byte[] PngBytes, int Width, int Height);
#pragma warning restore CA1819

/// <summary>Renders a scene to pixels. The implementation owns the drawing technology; the
///     domain owns the vocabulary and the validation, so a second renderer could replace the
///     first without a caller noticing.
///     <para>A FAILURE IS A TYPED RESULT, NEVER AN EXCEPTION: a scene arriving here has already
///     passed the scene validation, so a failure means the RENDERER could not produce pixels -
///     an unavailable font family, or an encoding fault - and the caller acts on the named
///     error exactly as it acts on any other tool error.</para></summary>
public interface IImageCompositionAccess
{
  /// <summary>Renders <paramref name="scene"/> to PNG bytes.</summary>
  /// <param name="scene">A scene that has passed validation.</param>
  /// <param name="ct">Cancels the render.</param>
  Task<Result<ComposedImage>> RenderAsync(SceneImage scene, CancellationToken ct = default);
}
