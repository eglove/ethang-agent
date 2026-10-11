using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Documents.ACL;

/// <summary>Decides whether a file may be opened as a docx (task 4). Zip magic bytes
///     first (extension never trusted), then package-part inspection: a vbaProject
///     part means macro document (MacroDocument refusal); a package that fails to
///     open or lacks a main document part is CorruptDocument. The caller opens the
///     package only after this passes.</summary>
public static class DocxFormatSniffer
{
  /// <summary>Stats the file, checks the 4-byte zip magic, then opens the package
  ///     read-only to inspect parts. Success value is the detected kind ("docx").</summary>
  public static Result<string> Inspect(string path)
  {
    if (!File.Exists(path))
    {
      return Result.Failure<string>(new DomainError(DocxErrorCodes.FileNotFound,
          $"File not found: '{path}'."));
    }

    FileInfo info = new(path);
    if (info.Length > DocxLimits.MaxFileBytes)
    {
      return Result.Failure<string>(new DomainError(DocxErrorCodes.OversizeDocument,
          $"File is {info.Length} bytes; the maximum is {DocxLimits.MaxFileBytes}."));
    }

    byte[] magic = new byte[4];
    using (FileStream fs = File.OpenRead(path))
    {
      int read = fs.Read(magic, 0, 4);
      if (read < 4 || magic[0] != 0x50 || magic[1] != 0x4B || magic[2] != 0x03 || magic[3] != 0x04)
      {
        return Result.Failure<string>(new DomainError(DocxErrorCodes.NotADocx,
            $"'{path}' is not a .docx package (zip magic bytes missing)."));
      }
    }

    try
    {
      using WordprocessingDocument doc = WordprocessingDocument.Open(path, false, new OpenSettings
      {
        MaxCharactersInPart = DocxLimits.MaxCharactersInPart,
      });
      return doc.MainDocumentPart is null
        ? Result.Failure<string>(new DomainError(DocxErrorCodes.CorruptDocument,
            $"'{path}' has no main document part - not a readable Word document."))
        : CheckMacroParts(doc);
    }
    catch (OpenXmlPackageException ex)
    {
      return Result.Failure<string>(new DomainError(DocxErrorCodes.CorruptDocument,
          $"'{path}' could not be opened as a Word package: {Clamp(ex.Message)}"));
    }
    catch (IOException ex)
    {
      return Result.Failure<string>(new DomainError(DocxErrorCodes.CorruptDocument,
          $"'{path}' could not be opened as a Word package: {Clamp(ex.Message)}"));
    }
  }

  /// <summary>Checks the vbaProject part AFTER a successful open (macro refusal).
  ///     Called by DirectDocxAccess before any work on an opened package.</summary>
  public static Result<string> CheckMacroParts(WordprocessingDocument doc)
  {
    ArgumentNullException.ThrowIfNull(doc);
    bool hasVba = HasPartRecursive(doc.Parts);
    return hasVba
      ? Result.Failure<string>(new DomainError(DocxErrorCodes.MacroDocument,
          "The document contains a vbaProject part (macro-enabled .docm content); macro documents are refused."))
      : Result.Success("docx");
  }

  private static bool HasPartRecursive(IEnumerable<IdPartPair> pairs)
    => pairs.Select(pair => pair.OpenXmlPart).Any(part => part is not null
      && (part.Uri.OriginalString.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)
      || HasPartRecursive(part.Parts)));

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }
}
