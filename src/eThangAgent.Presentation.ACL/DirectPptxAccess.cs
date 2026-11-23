using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL;

/// <summary>The IPptxAccess implementation over DocumentFormat.OpenXml. The outline read
///     is complete; every other member is a staged placeholder that throws
///     NotImplementedException until the task that owns it lands. Every method stats the
///     file FIRST and returns the content-true FileVersion; the sniffer gates every open.
///     </summary>
public sealed class DirectPptxAccess : IPptxAccess
{
  /// <summary>Reads the deck's outline: every slide in presentation order with its title,
  ///     first line, hidden flag and layout name, plus the deck's embedded media and its
  ///     distinct layout display names.</summary>
  public Task<Result<PptxOutline>> ReadOutlineAsync(string path, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    return Task.FromResult(ReadOutline(path));
  }

  /// <summary>Reads one slide: its shapes in document order with their addresses, its
  ///     speaker notes, the deck's media, its comments and its animation effects in
  ///     playback order. A slide number outside 1..SlideCount is SlideNotFound.</summary>
  public Task<Result<PptxSlideSnapshot>> ReadSlideAsync(string path, int slide, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    return Task.FromResult(ReadSlide(path, slide));
  }

  /// <summary>Staged placeholder: the atomic edit batch lands with its own task.</summary>
  public Task<Result<PptxEditOutcome>> EditAsync(string path, FileVersion expectedVersion, IReadOnlyList<PptxOperation> operations, CancellationToken ct = default)
    => throw new NotImplementedException("The pptx edit batch has not been implemented yet.");

  /// <summary>Staged placeholder: deck creation lands with its own task.</summary>
  public Task<Result<PptxSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default)
    => throw new NotImplementedException("Pptx creation has not been implemented yet.");

  /// <summary>Staged placeholder: comment reading lands with its own task.</summary>
  public Task<Result<IReadOnlyList<PptxCommentInfo>>> ListCommentsAsync(string path, CancellationToken ct = default)
    => throw new NotImplementedException("Pptx comment reading has not been implemented yet.");

  /// <summary>Staged placeholder: comment writing lands with its own task.</summary>
  public Task<Result<PptxSaveOutcome>> AddCommentAsync(string path, FileVersion expectedVersion, string address, string author, string text, CancellationToken ct = default)
    => throw new NotImplementedException("Pptx comment writing has not been implemented yet.");

  /// <summary>Staged placeholder: comment deletion lands with its own task.</summary>
  public Task<Result<PptxSaveOutcome>> DeleteCommentAsync(string path, FileVersion expectedVersion, int commentId, CancellationToken ct = default)
    => throw new NotImplementedException("Pptx comment deletion has not been implemented yet.");

  /// <summary>Staged placeholder: media extraction lands with its own task.</summary>
  public Task<Result<IReadOnlyList<ToolResultImage>>> ExtractImagesAsync(string path, IReadOnlyList<string>? names, CancellationToken ct = default)
    => throw new NotImplementedException("Pptx media extraction has not been implemented yet.");

  private static Result<PptxOutline> ReadOutline(string path)
  {
    if (!File.Exists(path))
    {
      return Failure(PptxErrorCodes.FileNotFound, $"File not found: '{path}'.");
    }

    FileInfo info = new(path);
    FileVersion version = new(info.Length, info.LastWriteTimeUtc);
    Result<string> sniff = PptxFormatSniffer.Inspect(path);
    return sniff.IsSuccess
      ? PptxOutlineReader.Read(path, version)
      : Result.Failure<PptxOutline>(sniff.Error);
  }

  private static Result<PptxSlideSnapshot> ReadSlide(string path, int slide)
  {
    if (!File.Exists(path))
    {
      return Result.Failure<PptxSlideSnapshot>(new DomainError(PptxErrorCodes.FileNotFound, $"File not found: '{path}'."));
    }

    FileInfo info = new(path);
    FileVersion version = new(info.Length, info.LastWriteTimeUtc);
    Result<string> sniff = PptxFormatSniffer.Inspect(path);
    return sniff.IsSuccess
      ? PptxSlideReader.Read(path, slide, version)
      : Result.Failure<PptxSlideSnapshot>(sniff.Error);
  }

  private static Result<PptxOutline> Failure(string code, string message)
    => Result.Failure<PptxOutline>(new DomainError(code, message));
}
