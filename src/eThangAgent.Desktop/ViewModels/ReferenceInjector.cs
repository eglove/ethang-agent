using System.Globalization;
using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>What one submitted message resolves to: the text the transcript shows, the read
///     tool results appended beneath it, and the notices for references that resolved to
///     nothing. A notice never stops the message: the user asked for the message to be sent,
///     and a reference that named nothing is information about the message, not a reason to
///     withhold it.</summary>
internal sealed record ReferenceInjectionResult(
    string MessageText,
    IReadOnlyList<ReferenceBlock> AppendedBlocks,
    IReadOnlyList<string> Notices,
    int ReferenceCount,
    int EstimatedTokens);

/// <summary>One reference's read result, exactly as the tool that read it produced it: the
///     tool's name, its own output format, and the images it attached (a picture, or a PDF
///     page rendered to PNG). The transcript shows this block and the model receives it, so a
///     reference reads the same way a tool call does.</summary>
internal sealed record ReferenceBlock(string ToolName, string Content, IReadOnlyList<ToolResultImage> Images)
{
  /// <summary>This block's text plus an estimate for its images, which have no text to count.
  ///     An image costs roughly a page of prose, so it is charged at the composer's own
  ///     characters-per-token ratio over a page's worth of characters.</summary>
  public int EstimatedCharacters => Content.Length + (Images.Count * ReferenceInjector.CharactersPerImage);

  /// <summary>The block as it travels to the model: the tool's text, then one marker line per
  ///     attached image naming what was attached and how large it is. The images themselves
  ///     travel as conversation parts, so the marker is the only place the model can see that
  ///     they exist.
  ///     <para><paramref name="imagesAttached"/> false is the STEERING case, where the message
  ///     carries text only: the marker then says the image did not travel and names the move
  ///     that fetches it, because a marker for bytes nobody received reads as a picture the
  ///     model is looking at and cannot see.</para></summary>
  public string ModelText(bool imagesAttached = true)
  {
    if (Images.Count == 0)
    {
      return Content;
    }

    System.Text.StringBuilder sb = new(Content);
    foreach (ToolResultImage image in Images)
    {
      _ = sb.AppendLine();
      _ = sb.Append(ImageMarker(image, imagesAttached));
    }

    return sb.ToString();
  }

  /// <summary>One image's marker: the media type, the decoded size, and - when the bytes did not
  ///     travel - what to do instead.</summary>
  private static string ImageMarker(ToolResultImage image, bool imagesAttached)
  {
    string size = ImagePartMarker.DecodedByteCount(image.Base64Data).ToString(CultureInfo.InvariantCulture);
    return imagesAttached
        ? $"[image: {image.MediaType}, {size} bytes]"
        : $"[image: {image.MediaType}, {size} bytes - not attached to this message; "
            + "the annotation above names the file, read it with that tool]";
  }
}

/// <summary>Resolves every <c>@</c> reference in a submitted message: a catalog skill name
///     wins over a workspace file, and a file renders through the READ TOOL THAT OWNS ITS
///     KIND - the read tool for text, read_image for a picture, pdf for a document, docx for a
///     Word file. A reference is a read, so it produces what that tool produces, images
///     included; a file no read tool serves is refused by name rather than decoded as text.
///     The freshness ledger is deliberately NOT touched: it is the agent's per-turn memory,
///     reset at turn start, so a record made here would be cleared before the model ever read
///     from it.
///     <para>A QUOTED path (<c>@"C:\..."</c>) is granted to the session before it is read, so
///     the same message that names an external file can also have it opened. The grant is the
///     quoted path's own effect - a bare path in prose grants nothing - and it is read-only,
///     because naming a file says what to look at and never that it may be written.</para>
///     <para>A reference that resolves to nothing posts a NOTICE and is skipped. It used to
///     refuse the whole message, which made a typo or a stale path cost the user everything
///     they had typed; the message now sends and the notice says what was left out.</para></summary>
internal sealed class ReferenceInjector(
    IFileSystemAccess files,
    SkillInvocationService? skills,
    string workspaceRoot,
    SessionPathGrants? grants = null,
    AgentToolsProvider? tools = null,
    IPathResolver? trustedRoots = null)
{
  /// <summary>The tool that reads an <c>@</c> reference's kind: the read tool for text,
  ///     read_image for a picture, pdf for a document, docx for a Word file.</summary>
  private const string TextTool = "read";

  private const string ImageTool = "read_image";

  private const string PdfToolName = "pdf";

  private const string DocxToolName = "docx";

  /// <summary>The budget every reference read is given. The tool contract requires one, and a
  ///     reference has no model to choose it: a page of a file is a bounded read, so the budget
  ///     is generous enough that a legitimate read never reaches it and small enough that a
  ///     wedged one fails instead of holding the submit.</summary>
  private const int ReadBudgetSeconds = 60;

  private const int PageLines = 1000;

  private const int PdfMaxPages = 20;

  private const int DocxMaxBlocks = 200;

  /// <summary>The rough characters-per-token ratio the composer's cost readout divides by.</summary>
  public const int CharactersPerToken = 4;

  /// <summary>What one attached image is charged at in the cost readout, in characters: a page
  ///     of prose. An image has no text to count, and a readout that ignored it would call a
  ///     referenced screenshot free.</summary>
  public const int CharactersPerImage = 1000;

  private static readonly string[] ImageExtensions =
      [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico"];

  private static readonly string[] PdfExtensions = [".pdf"];

  private static readonly string[] DocxExtensions = [".docx"];

  /// <summary>Extensions no read tool opens: containers, executables, databases, media, fonts,
  ///     and the office formats this harness has no reader for. Everything else is TEXT, and
  ///     the bytes get the final say - a binary carrying an extension not listed here is still
  ///     refused by the reader rather than decoded into replacement characters.</summary>
  private static readonly string[] UnreadableExtensions =
  [
      // Archives and packages
      ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab", ".iso",
      ".jar", ".war", ".nupkg", ".whl", ".apk", ".ipa", ".deb", ".rpm", ".msix", ".appx",
      // Executables and libraries
      ".exe", ".dll", ".so", ".dylib", ".pdb", ".lib", ".obj", ".sys", ".msi", ".bin", ".dat",
      // Databases and indexes
      ".db", ".sqlite", ".sqlite3", ".mdb", ".ldb",
      // Media
      ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma", ".mp4", ".mov", ".avi", ".mkv",
      ".webm", ".wmv", ".flv", ".m4v",
      // Formats a picture reader cannot open (the image tool sniffs PNG and JPEG bytes only)
      ".psd", ".svgz", ".heic", ".heif", ".raw", ".cr2", ".nef", ".dng",
      // Documents with no reader here
      ".doc", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".epub",
      // Fonts and compiled resources
      ".ttf", ".otf", ".woff", ".woff2", ".eot", ".resx", ".resources", ".class", ".pyc",
  ];

  private readonly IFileSystemAccess _files = files ?? throw new ArgumentNullException(nameof(files));
  private readonly SkillInvocationService? _skills = skills;
  private readonly SessionPathGrants? _grants = grants;
  private readonly WorkspacePathResolver _resolver = new(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
  private readonly AgentToolsProvider? _tools = tools;
  private readonly IPathResolver? _trustedRoots = trustedRoots;

  public async Task<ReferenceInjectionResult> InjectAsync(string input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    List<ReferenceBlock> blocks = [];
    List<string> notices = [];
    int references = 0;

    // The grant is made BEFORE anything is read, so a quoted path is reachable by the very
    // message that quoted it - and by every later message, which is what makes it a session
    // grant rather than a one-shot resolution.
    List<string> quoted = ReferenceTokenParser.QuotedPaths(input);
    foreach (string path in quoted)
    {
      _grants?.Grant(path);
    }

    foreach (string name in Names(input))
    {
      Result<string> skill = await SkillBlockAsync(name, ct).ConfigureAwait(true);
      if (skill.IsSuccess)
      {
        blocks.Add(new ReferenceBlock("skill", skill.Value, []));
        references++;
        continue;
      }

      Result<ReferenceBlock> file = await FileBlockAsync(name, ct).ConfigureAwait(true);
      if (!file.IsSuccess)
      {
        notices.Add(file.Error.Message);
        continue;
      }

      blocks.Add(file.Value);
      references++;
    }

    // Quoted paths resolve as FILES only: the quotes say 'this is a path', so a quoted word
    // that happens to match a skill name is still a path and must not invoke anything.
    foreach (string path in quoted)
    {
      Result<ReferenceBlock> file = await FileBlockAsync(path, ct).ConfigureAwait(true);
      if (!file.IsSuccess)
      {
        notices.Add(file.Error.Message);
        continue;
      }

      blocks.Add(file.Value);
      references++;
    }

    int characters = blocks.Sum(b => b.EstimatedCharacters);
    return new ReferenceInjectionResult(
        ReferenceTokenParser.Unescape(input), blocks, notices, references, characters / CharactersPerToken);
  }

  /// <summary>Every unescaped <c>@</c> token in the message, in order, without its trigger.</summary>
  public static List<string> Names(string input)
  {
    List<string> names = [];
    int i = 0;
    while (i < input.Length)
    {
      if (input[i] != '@')
      {
        i++;
        continue;
      }

      if (i + 1 < input.Length && input[i + 1] == '@')
      {
        i += 2; // escaped literal: never resolved
        continue;
      }

      int end = i + 1;
      while (end < input.Length && IsTokenCharacter(input[end]))
      {
        end++;
      }

      if (end > i + 1 && (i == 0 || IsWordBoundary(input[i - 1])))
      {
        names.Add(input[(i + 1)..end]);
      }

      i = end;
    }

    return names;
  }

  /// <summary>The tool that reads a file of this name, or null when no read tool serves it.
  ///     A reference reads what a read tool can open and nothing else: a picture, a PDF, a Word
  ///     document, or text. Text is the default rather than a list, because text files are
  ///     named by every convention there is and an allow-list of extensions would refuse most
  ///     of them; the formats that are definitively not text are the list, and the BYTES are the
  ///     final authority either way, so a binary carrying an unfamiliar extension is refused by
  ///     the reader instead of decoded into a page of replacement characters.</summary>
  public static string? ToolFor(string name)
  {
    ArgumentNullException.ThrowIfNull(name);
    string extension = Path.GetExtension(name);
    return extension.Length == 0 ? TextTool : ToolForExtension(extension);
  }

  /// <summary>Extensions are matched without case: a camera writes IMG_0001.JPG and the file
  ///     is still a picture.</summary>
  private static string? ToolForExtension(string extension) => extension switch
  {
    _ when ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) => ImageTool,
    _ when PdfExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) => PdfToolName,
    _ when DocxExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) => DocxToolName,
    _ when UnreadableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) => null,
    _ => TextTool,
  };

  private async Task<Result<string>> SkillBlockAsync(string name, CancellationToken ct)
  {
    if (_skills is null)
    {
      return Result.Failure<string>(new DomainError("SkillNotFound", $"No skill named '{name}'."));
    }

    Result<SkillInvocation> invoked = await _skills.InvokeAsync(name, null, ct).ConfigureAwait(true);
    return invoked.IsSuccess
        ? Result.Success(invoked.Value.SystemLine)
        : Result.Failure<string>(invoked.Error);
  }

  /// <summary>One reference's read result, or the notice that says why it is absent. A path
  ///     outside the workspace is not a fault either: the notice names the refusal so the
  ///     model and the user learn the session's reach from it.</summary>
  private async Task<Result<ReferenceBlock>> FileBlockAsync(string name, CancellationToken ct)
  {
    if (ToolFor(name) is not { } toolName)
    {
      return Result.Failure<ReferenceBlock>(new DomainError("ReferenceUnreadable",
          $"Error [ReferenceUnreadable]: '@{name}' is not a file any read tool opens. "
          + "References read text files, images (png, jpeg), PDFs, and .docx documents."));
    }

    Result<string> resolved = ResolveReference(name);
    if (!resolved.IsSuccess)
    {
      return Result.Failure<ReferenceBlock>(resolved.Error.Code == "PathOutsideWorkspace"
          ? new DomainError("ReferenceOutsideWorkspace",
              $"Error [ReferenceOutsideWorkspace]: '@{name}' resolves outside this workspace, "
              + "and only a quoted path grants a location beyond it.")
          : resolved.Error);
    }

    string path = resolved.Value;
    if (_tools is not null)
    {
      return await ReadWithToolAsync(toolName, path, name, ct).ConfigureAwait(true);
    }

    // No tool surface (a session that registered none): the one reader this class owns is the
    // line reader, so text still resolves and every other kind says why it cannot.
    return toolName == TextTool
        ? await ReadTextAsync(path, ct).ConfigureAwait(true)
        : Result.Failure<ReferenceBlock>(new DomainError("ReferenceUnreadable",
            $"Error [ReferenceUnreadable]: '@{name}' needs the {toolName} tool, and this session "
            + "wired no tool surface."));
  }

  /// <summary>Runs the tool that owns the reference's kind and returns what it produced. A
  ///     tool refusal is the reference's notice: the tool already named what was wrong with
  ///     the file (its bytes are not that format, it is too large, it has no pages).</summary>
  private async Task<Result<ReferenceBlock>> ReadWithToolAsync(string toolName, string path,
      string name, CancellationToken ct)
  {
    string arguments = ReadArguments(toolName, path);
    CapabilityInvocationResult result = await _tools!
        .InvokeAsync(toolName, arguments, ct).ConfigureAwait(true);
    return result.IsError
        ? Result.Failure<ReferenceBlock>(new DomainError("ReferenceUnreadable",
            $"Error [ReferenceUnreadable]: '@{name}' could not be read: {result.Content}"))
        : Result.Success(new ReferenceBlock(toolName, result.Content, result.Images ?? []));
  }

  /// <summary>The read tool's own arguments for one reference: one page of a text file, the
  ///     first page of a document, and the whole of a picture.</summary>
  private static string ReadArguments(string toolName, string path) => toolName switch
  {
    TextTool => JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
    {
      [ToolTimeout.ParameterName] = ReadBudgetSeconds,
      ["path"] = path,
      ["startLine"] = 1,
      ["endLine"] = PageLines,
    }),
    PdfToolName => JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
    {
      [ToolTimeout.ParameterName] = ReadBudgetSeconds,
      ["action"] = "read",
      ["path"] = path,
      ["offset"] = 0,
      ["maxPages"] = PdfMaxPages,
    }),
    DocxToolName => JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
    {
      [ToolTimeout.ParameterName] = ReadBudgetSeconds,
      ["action"] = "read",
      ["path"] = path,
      ["offset"] = 0,
      ["maxBlocks"] = DocxMaxBlocks,
    }),
    _ => JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
    {
      [ToolTimeout.ParameterName] = ReadBudgetSeconds,
      ["path"] = path,
    }),
  };

  /// <summary>The tool-less text read: the read tool's own output format for one page, so a
  ///     session with no tool surface renders a reference exactly as a tool would.</summary>
  private async Task<Result<ReferenceBlock>> ReadTextAsync(string path, CancellationToken ct)
  {
    Result<FileRead> read = await _files.ReadLinesAsync(path, 1, PageLines, ct).ConfigureAwait(true);
    return read.IsSuccess
        ? Result.Success(new ReferenceBlock(TextTool,
            ReadOutputRenderer.Render(path, 1, PageLines, read.Value, new FileCoverage([]), forced: false), []))
        : Result.Failure<ReferenceBlock>(new DomainError("ReferenceNotFound",
            $"Error [ReferenceNotFound]: '{path}' could not be read: {read.Error.Message}"));
  }

  /// <summary>Resolves one reference through the workspace first, then the session's quoted-path
  ///     grants, so an external path works only because the user quoted it. The trusted roots
  ///     are consulted last, because they are what the READ TOOLS resolve against: a reference
  ///     the tools could open must not be refused here.</summary>
  private Result<string> ResolveReference(string name)
  {
    Result<string> resolved = _resolver.Resolve(name);
    if (resolved.IsSuccess)
    {
      return resolved;
    }

    if (_trustedRoots is not null)
    {
      Result<string> trusted = _trustedRoots.Resolve(name);
      if (trusted.IsSuccess)
      {
        return trusted;
      }
    }

    IReadOnlyList<TrustedDirectory> granted = _grants?.List ?? [];
    return granted.Count == 0
        ? resolved
        : new TrustedDirectoryPathResolver(_resolver, granted, enforceWritePolicy: false).Resolve(name);
  }

  private static bool IsWordBoundary(char value)
      => char.IsWhiteSpace(value) || value is '(' or '[' or '"' or '@';

  private static bool IsTokenCharacter(char value)
      => char.IsLetterOrDigit(value) || value is '-' or '_' or '.' or '/' or '\\';
}
