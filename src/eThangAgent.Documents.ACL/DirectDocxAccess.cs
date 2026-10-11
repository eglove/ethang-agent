using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Documents.ACL;

/// <summary>The IDocxAccess implementation over DocumentFormat.OpenXml (task 5 ships
///     the read path; later tasks replace their own NotSupportedException
///     placeholder - plan-mandated staging). Every method stats the file FIRST and
///     returns the content-true FileVersion; the sniffer gates every open.</summary>
public sealed class DirectDocxAccess : IDocxAccess
{
  /// <summary>Reads one page of the flat body block list plus document metadata.</summary>
  public Task<Result<DocxSnapshot>> ReadAsync(string path, int offset, int maxBlocks, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    return Task.FromResult((offset < 0 || maxBlocks is < 1 or > DocxLimits.MaxBlocksPerPage)
      ? InvalidSnapshot(
        $"offset must be >= 0 and maxBlocks must be 1..{DocxLimits.MaxBlocksPerPage}; got offset {offset}, maxBlocks {maxBlocks}.")
      : ReadCore(path, offset, maxBlocks));
  }

  public Task<Result<DocxEditOutcome>> EditAsync(string path, FileVersion expectedVersion, IReadOnlyList<DocxOperation> operations, CancellationToken ct = default)
    => throw new NotSupportedException("EditAsync is implemented by task 6 (edit ops part 1).");

  public Task<Result<DocxSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default)
    => throw new NotSupportedException("CreateAsync is implemented by task 8 (create from markdown).");

  public Task<Result<IReadOnlyList<DocxCommentInfo>>> ListCommentsAsync(string path, CancellationToken ct = default)
    => throw new NotSupportedException("ListCommentsAsync is implemented by task 9 (comments).");

  public Task<Result<DocxSaveOutcome>> AddCommentAsync(string path, FileVersion expectedVersion, string address, string author, string text, CancellationToken ct = default)
    => throw new NotSupportedException("AddCommentAsync is implemented by task 9 (comments).");

  public Task<Result<DocxSaveOutcome>> DeleteCommentAsync(string path, FileVersion expectedVersion, int commentId, CancellationToken ct = default)
    => throw new NotSupportedException("DeleteCommentAsync is implemented by task 9 (comments).");

  public Task<Result<IReadOnlyList<DocxRevisionInfo>>> ListRevisionsAsync(string path, CancellationToken ct = default)
    => throw new NotSupportedException("ListRevisionsAsync is implemented by task 10 (revisions).");

  public Task<Result<DocxSaveOutcome>> ResolveRevisionsAsync(string path, FileVersion expectedVersion, bool accept, DocxRevisionScope scope, CancellationToken ct = default)
    => throw new NotSupportedException("ResolveRevisionsAsync is implemented by task 10 (revisions).");

  public Task<Result<IReadOnlyList<ToolResultImage>>> ExtractImagesAsync(string path, IReadOnlyList<string>? names, CancellationToken ct = default)
    => throw new NotSupportedException("ExtractImagesAsync is implemented by task 11 (extract images).");

  private static Result<DocxSnapshot> ReadCore(string path, int offset, int maxBlocks)
  {
    if (!File.Exists(path))
    {
      return FailSnapshot(DocxErrorCodes.FileNotFound, $"File not found: '{path}'.");
    }

    FileInfo info = new(path);
    FileVersion version = new(info.Length, info.LastWriteTimeUtc);
    Result<string> sniff = DocxFormatSniffer.Inspect(path);
    if (!sniff.IsSuccess)
    {
      return Result.Failure<DocxSnapshot>(sniff.Error);
    }

    try
    {
      using WordprocessingDocument doc = WordprocessingDocument.Open(path, false, new OpenSettings
      {
        MaxCharactersInPart = DocxLimits.MaxCharactersInPart,
      });
      return doc.MainDocumentPart?.Document?.Body is null
        ? FailSnapshot(DocxErrorCodes.CorruptDocument, $" '{path}' has no readable document body.")
        : Result.Success(DocxBlockWalker.Walk(doc, version, offset, maxBlocks));
    }
    catch (OpenXmlPackageException ex)
    {
      return FailSnapshot(DocxErrorCodes.CorruptDocument, $" '{path}' could not be opened as a Word package: {Clamp(ex.Message)}");
    }
    catch (IOException ex)
    {
      return FailSnapshot(DocxErrorCodes.CorruptDocument, $" '{path}' could not be opened as a Word package: {Clamp(ex.Message)}");
    }
  }

  private static Result<DocxSnapshot> InvalidSnapshot(string message)
    => FailSnapshot(ToolErrorCodes.InvalidParameterValue, message);

  private static Result<DocxSnapshot> FailSnapshot(string code, string message)
    => Result.Failure<DocxSnapshot>(new DomainError(code, message));

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }
}
