using System.IO.Compression;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL;

/// <summary>Decides whether a file may be opened as a presentation. The 4-byte zip magic
///     is checked first and the extension is never trusted; the package's part list then
///     decides the kind. A vbaProject part means macro-enabled content (MacroPresentation);
///     a package that cannot be read is CorruptPresentation; a zip that carries no
///     ppt/presentation.xml part is NotAPptx. The caller opens the package only after this
///     passes.</summary>
public static class PptxFormatSniffer
{
  /// <summary>The part whose presence makes a zip package a presentation.</summary>
  public const string PresentationPartName = "ppt/presentation.xml";

  /// <summary>The part whose presence makes a presentation macro-enabled.</summary>
  public const string MacroPartName = "ppt/vbaProject.bin";

  /// <summary>The one extension a deck may carry. A .ppsx slide show and a .potx template
  ///     are PresentationML packages too, and they are refused rather than converted.
  ///     </summary>
  public const string DeckExtension = ".pptx";

  private const string PresentationKind = "pptx";

  /// <summary>Stats the file, checks the size cap, checks the 4-byte zip magic, then
  ///     reads the package's part list. Success value is the detected kind ("pptx").
  ///     </summary>
  public static Result<string> Inspect(string path) => CheckFile(path) ?? InspectPackage(path);

  private static Result<string>? CheckFile(string path)
  {
    if (!File.Exists(path))
    {
      return Failure(PptxErrorCodes.FileNotFound, $"File not found: '{path}'.");
    }

    FileInfo info = new(path);
    return info.Length > PptxLimits.MaxFileBytes
      ? Failure(PptxErrorCodes.OversizePresentation,
          $"File is {info.Length} bytes; the maximum is {PptxLimits.MaxFileBytes}.")
      : CheckMagic(path);
  }

  private static Result<string>? CheckMagic(string path)
    => HasZipMagic(path)
      ? null
      : Failure(PptxErrorCodes.NotAPptx, $"'{path}' is not a .pptx package (zip magic bytes missing).");

  private static bool HasZipMagic(string path)
  {
    byte[] magic = new byte[4];
    using FileStream fs = File.OpenRead(path);
    int read = fs.Read(magic, 0, 4);
    return read == 4 && magic[0] == 0x50 && magic[1] == 0x4B && magic[2] == 0x03 && magic[3] == 0x04;
  }

  private static Result<string> InspectPackage(string path)
  {
    try
    {
      using ZipArchive archive = ZipFile.OpenRead(path);
      return Classify(path, archive.Entries.Select(entry => entry.FullName));
    }
    catch (InvalidDataException ex)
    {
      return Failure(PptxErrorCodes.CorruptPresentation, Unreadable(path, ex.Message));
    }
    catch (IOException ex)
    {
      return Failure(PptxErrorCodes.CorruptPresentation, Unreadable(path, ex.Message));
    }
    catch (UnauthorizedAccessException ex)
    {
      return Failure(PptxErrorCodes.CorruptPresentation, Unreadable(path, ex.Message));
    }
  }

  private static Result<string> Classify(string path, IEnumerable<string> partNames)
  {
    string[] names = [.. partNames.Select(name => name.Replace('\\', '/'))];
    bool hasPresentation = names.Contains(PresentationPartName, StringComparer.OrdinalIgnoreCase);
    bool hasMacro = names.Contains(MacroPartName, StringComparer.OrdinalIgnoreCase);
    return (hasPresentation, hasMacro) switch
    {
      (false, _) => Failure(PptxErrorCodes.NotAPptx,
          $"The package carries no {PresentationPartName} part - not a PowerPoint presentation."),
      (true, true) => Failure(PptxErrorCodes.MacroPresentation,
          $"The presentation contains a {MacroPartName} part (macro-enabled .pptm content); macro presentations are refused."),
      _ => CheckExtension(path),
    };
  }

  private static Result<string> CheckExtension(string path)
  {
    string extension = Path.GetExtension(path);
    return string.Equals(extension, DeckExtension, StringComparison.OrdinalIgnoreCase)
      ? Result.Success(PresentationKind)
      : Failure(PptxErrorCodes.NotAPptx,
          $"'{extension}' files are refused; only {DeckExtension} presentations are supported.");
  }

  private static string Unreadable(string path, string message)
    => $"'{path}' could not be read as a package: {Clamp(message)}";

  private static Result<string> Failure(string code, string message)
    => Result.Failure<string>(new DomainError(code, message));

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }
}
