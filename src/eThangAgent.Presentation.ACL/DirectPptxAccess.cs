using DocumentFormat.OpenXml.Packaging;
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

  /// <summary>Applies an atomic edit batch: the version is checked against the file
  ///     BEFORE anything else, the batch is resolved and applied to a copy, and only a
  ///     fully saved copy replaces the original. A refused batch leaves the file
  ///     byte-identical.</summary>
  public Task<Result<PptxEditOutcome>> EditAsync(string path, FileVersion expectedVersion, IReadOnlyList<PptxOperation> operations, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(expectedVersion);
    ArgumentNullException.ThrowIfNull(operations);
    return Task.FromResult(EditCore(path, expectedVersion, operations));
  }

  /// <summary>Creates a new deck from the pinned markdown subset: the file must not
  ///     exist and its parent directory must, a source with no text is MarkdownInvalid,
  ///     and a line of three hyphens separates slides.</summary>
  public Task<Result<PptxSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(markdown);
    return Task.FromResult(CreateCore(path, markdown));
  }

  /// <summary>Lists every comment in the deck, slide by slide in presentation order:
  ///     each with its author, text, the address of the shape it anchors to and whether it
  ///     came from the legacy comment shape. A deck carrying both shapes lists both.</summary>
  public Task<Result<IReadOnlyList<PptxCommentInfo>>> ListCommentsAsync(string path, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    return Task.FromResult(PptxComments.List(path));
  }

  /// <summary>Adds a comment in the CURRENT shape, anchored at the addressed shape, and
  ///     saves the deck. Legacy comments the deck already carries are left as they are. A
  ///     version that is not the file's current one is StalePresentation.</summary>
  public Task<Result<PptxSaveOutcome>> AddCommentAsync(string path, FileVersion expectedVersion, string address, string author, string text, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(expectedVersion);
    ArgumentNullException.ThrowIfNull(author);
    ArgumentNullException.ThrowIfNull(text);
    return Task.FromResult(PptxComments.Add(path, expectedVersion, address, author, text));
  }

  /// <summary>Deletes one comment by id, from whichever shape holds it, and saves the
  ///     deck. An id that names no comment is CommentNotFound; a version that is not the
  ///     file's current one is StalePresentation.</summary>
  public Task<Result<PptxSaveOutcome>> DeleteCommentAsync(string path, FileVersion expectedVersion, int commentId, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(expectedVersion);
    return Task.FromResult(PptxComments.Delete(path, expectedVersion, commentId));
  }

  /// <summary>Extracts the deck's media as model-attachable images: every PNG and JPEG
  ///     part, or the subset the names select. A listed name the deck does not carry, and a
  ///     media part that is neither PNG nor JPEG, are both ImageNotAttachable.</summary>
  public Task<Result<IReadOnlyList<ToolResultImage>>> ExtractImagesAsync(string path, IReadOnlyList<string>? names, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    return Task.FromResult(PptxMediaWriter.Extract(path, names));
  }

  /// <summary>The create preconditions that fail before anything is written: the target
  ///     must not exist and its parent directory must.</summary>
  private static Result<PptxSaveOutcome> CreateCore(string path, string markdown)
  {
    if (File.Exists(path))
    {
      return FailSave(ToolErrorCodes.InvalidParameterValue,
          $"'{path}' already exists; create is for new paths (delete first is the model's explicit move).");
    }

    string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
    return directory is not null && !Directory.Exists(directory)
      ? FailSave(PptxErrorCodes.FileNotFound, $"Directory not found: '{directory}'.")
      : PptxDeckBuilder.Build(path, markdown);
  }

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

  /// <summary>The edit preconditions that fail before the package is opened: the
  ///     operation count bounds, the file's existence, the version equality and the
  ///     sniffer gate. Null when every precondition holds.</summary>
  private static Result<PptxEditOutcome>? PreflightEdit(string path, FileVersion expectedVersion,
      IReadOnlyList<PptxOperation> operations)
  {
    if (operations.Count == 0)
    {
      return FailEdit(ToolErrorCodes.InvalidParameterValue, "The operations list must not be empty.");
    }

    if (operations.Count > PptxLimits.MaxOperationsPerEdit)
    {
      return FailEdit(ToolErrorCodes.InvalidParameterValue,
          $"The operations list exceeds the {PptxLimits.MaxOperationsPerEdit}-operation cap.");
    }

    if (!File.Exists(path))
    {
      return FailEdit(PptxErrorCodes.FileNotFound, $"File not found: '{path}'.");
    }

    FileInfo info = new(path);
    FileVersion current = new(info.Length, info.LastWriteTimeUtc);
    if (current != expectedVersion)
    {
      return FailEdit(PptxErrorCodes.StalePresentation,
          $"The presentation changed since it was read (expected {expectedVersion.Token}, current {current.Token}); re-read and retry.");
    }

    Result<string> sniff = PptxFormatSniffer.Inspect(path);
    return sniff.IsSuccess ? null : Result.Failure<PptxEditOutcome>(sniff.Error);
  }

  /// <summary>Applies the batch to a copy of the deck and moves the saved copy over the
  ///     original. The package is closed before the move, so no part stream is open while
  ///     the file is replaced, and a failure at any point before the move deletes the copy
  ///     and leaves the original untouched.</summary>
  private static Result<PptxEditOutcome> EditCore(string path, FileVersion expectedVersion,
      IReadOnlyList<PptxOperation> operations)
  {
    Result<PptxEditOutcome>? preflight = PreflightEdit(path, expectedVersion, operations);
    if (preflight is not null)
    {
      return preflight;
    }

    string copy = TempPathFor(path);
    try
    {
      File.Copy(path, copy, true);
      Result<int> applied = ApplyToCopy(copy, operations);
      if (!applied.IsSuccess)
      {
        return Result.Failure<PptxEditOutcome>(applied.Error);
      }

      File.Move(copy, path, true);
      return Result.Success(new PptxEditOutcome(StatAfter(path), applied.Value));
    }
    catch (OpenXmlPackageException ex)
    {
      return FailEdit(PptxErrorCodes.CorruptPresentation, $"'{path}' could not be opened as a presentation: {Clamp(ex.Message)}");
    }
    catch (InvalidDataException ex)
    {
      return FailEdit(PptxErrorCodes.CorruptPresentation, $"'{path}' could not be opened as a presentation: {Clamp(ex.Message)}");
    }
    catch (IOException ex)
    {
      return FailEdit(PptxErrorCodes.CorruptPresentation, $"'{path}' could not be opened as a presentation: {Clamp(ex.Message)}");
    }
    finally
    {
      TryDelete(copy);
    }
  }

  private static Result<int> ApplyToCopy(string copy, IReadOnlyList<PptxOperation> operations)
  {
    using PresentationDocument doc = PresentationDocument.Open(copy, true, new OpenSettings
    {
      MaxCharactersInPart = PptxLimits.MaxCharactersInPart,
    });
    Result<int> applied = PptxEditBatch.Apply(doc, operations);
    if (applied.IsSuccess)
    {
      doc.Save();
    }

    return applied;
  }

  private static FileVersion StatAfter(string path)
  {
    FileInfo after = new(path);
    return new FileVersion(after.Length, after.LastWriteTimeUtc);
  }

  /// <summary>A sibling path the copy is written to before it replaces the original.
  ///     </summary>
  private static string TempPathFor(string path)
  {
    string full = Path.GetFullPath(path);
    string directory = Path.GetDirectoryName(full) ?? ".";
    return Path.Combine(directory, Path.GetFileName(full) + ".tmp-" + Guid.NewGuid().ToString("N"));
  }

  private static void TryDelete(string path)
  {
    try
    {
      if (File.Exists(path))
      {
        File.Delete(path);
      }
    }
    catch (IOException)
    {
      // best-effort cleanup: the original is already intact
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort cleanup: the original is already intact
    }
  }

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }

  private static Result<PptxEditOutcome> FailEdit(string code, string message)
    => Result.Failure<PptxEditOutcome>(new DomainError(code, message));

  private static Result<PptxOutline> Failure(string code, string message)
    => Result.Failure<PptxOutline>(new DomainError(code, message));

  private static Result<PptxSaveOutcome> FailSave(string code, string message)
    => Result.Failure<PptxSaveOutcome>(new DomainError(code, message));
}
