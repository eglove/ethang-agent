using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The pdf tool (task 3): one action-switch over the IPdfAccess seam -
///     read, render, create, manipulate. Strict input parsing via PdfToolInput;
///     workspace path resolution with worktree anchoring via IWorkspaceScopedTool;
///     turn-local read-freshness ledger participation on read; the version token
///     (v&lt;length&gt;:&lt;ticks&gt;) is the model's handle for every version-guarded
///     manipulate. Render attaches PNG page images only when the resolved model
///     accepts image input (IImageInputCapability), else lists the pages. The
///     description below is the verbatim format contract the model reads; the
///     renderers implement it exactly.</summary>
public sealed class PdfTool(
    IPathResolver resolver,
    IPdfAccess pdf,
    IImageInputCapability? vision,
    ReadFreshnessLedger? ledger = null) : ITool, IWorkspaceScopedTool, ILedgerBoundTool
{
  private const string ToolName = "pdf";

  private const string PngMediaType = "image/png";

  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

  // Actions that never write the document (issue #96): read extracts text, render
  // rasterizes to attached PNGs. create and manipulate write, so they keep the strict
  // resolver. Deny by default: an action not named here is treated as mutating.
  private static readonly IReadOnlySet<string> ReadOnlyActions =
      new HashSet<string>(StringComparer.Ordinal) { "read", "render", "form" };
  private readonly IPdfAccess _pdf = pdf ?? throw new ArgumentNullException(nameof(pdf));
  private readonly IImageInputCapability? _vision = vision;
  private readonly ReadFreshnessLedger? _ledger = ledger;

  /// <inheritdoc />
  public ITool RootedAt(string workspaceRoot)
      => new PdfTool(new WorkspacePathResolver(workspaceRoot), _pdf, _vision, _ledger);

  /// <inheritdoc />
  public bool HasLedger => _ledger is not null;

  /// <inheritdoc />
  public ITool WithLedger(ReadFreshnessLedger ledger)
      => _ledger is null ? new PdfTool(_resolver, _pdf, _vision, ledger) : this;

  public ToolDefinition Definition { get; } = new(
      ToolName,
      BuildDescription(),
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("action", ToolParameterType.Text,
                "Exactly one of read, render, create, manipulate, form (case-sensitive)."),
            new ToolParameter("path", ToolParameterType.Text,
                "Document path, workspace-relative or absolute-inside-workspace. Required for every action."),
            new ToolParameter("offset", ToolParameterType.WholeNumber,
                "Optional, read only: page offset into the document (default 0)."),
            new ToolParameter("maxPages", ToolParameterType.WholeNumber,
                "Optional, read only: maximum pages per read, 1..100 (default 20)."),
            new ToolParameter("pages", ToolParameterType.Text,
                "Render only: the 1-based page-range string ('1-3,5,8-', open tail allowed)."),
            new ToolParameter("dpi", ToolParameterType.WholeNumber,
                "Optional, render only: raster DPI, 1..300 (default 150)."),
            new ToolParameter("expectedVersion", ToolParameterType.Text,
                "The exact 'version v<length>:<ticks>' token a previous pdf read returned. Required for manipulate."),
            new ToolParameter("operations", ToolParameterType.Text,
                "Optional, manipulate only: array of operation objects (see the contract in the description)."),
            new ToolParameter("markdown", ToolParameterType.Text,
                "Optional, create only: the pinned-subset markdown source."),
      ],
      [ToolTimeout.ParameterName, "action", "path"]);

  private static string BuildDescription()
  {
    StringBuilder sb = new();
    _ = sb.AppendLine("pdf - read, render, create, and manipulate PDF documents, including their AcroForm fields.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("Actions (parameter 'action', required):");
    _ = sb.AppendLine("  read        Paginated per-page text. Params: path, offset (page offset, default 0), maxPages (default 20, max 100).");
    _ = sb.AppendLine("  render      Pages as PNG images (attached when the model accepts images). Params: path, pages ('1-3,5,8-' 1-based, open tail allowed), dpi (default 150, max 300). Max 10 pages per call.");
    _ = sb.AppendLine("  create      New PDF from the pinned markdown subset. Params: path, markdown.");
    _ = sb.AppendLine("  manipulate  Atomic batch of structural operations. Params: path, expectedVersion, operations[].");
    _ = sb.AppendLine("  form        The document's AcroForm fields: name, kind, page, current value, options. Params: path.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("read output (verbatim contract):");
    _ = sb.AppendLine("[pdf <path> v<version> pages <first>-<last> of <total>]");
    _ = sb.AppendLine("--- page 1 (612x792pt) ---");
    _ = sb.AppendLine("<text lines>");
    _ = sb.AppendLine("version v<length>:<ticks>");
    _ = sb.AppendLine("The 'version v<...>' line is the token EVERY manipulate call must pass back as expectedVersion.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("manipulate operations (each object needs 'op'):");
    _ = sb.AppendLine("  rotate {pages, degrees}          - degrees exactly 90, 180, or 270");
    _ = sb.AppendLine("  deletePages {pages}");
    _ = sb.AppendLine("  reorderPages {order}             - explicit full permutation of the document's pages");
    _ = sb.AppendLine("  extractPages {pages, outputPath} - writes a new PDF; the output must not already exist");
    _ = sb.AppendLine("  merge {sources[]}                - appends other PDFs into the target");
    _ = sb.AppendLine("  watermarkText {text, fontSize?, opacity?, degrees?} - diagonal stamp; opacity 0.0-1.0");
    _ = sb.AppendLine("  setMetadata {title?, author?, subject?}");
    _ = sb.AppendLine("  fillFormField {field, value}       - value follows the field's kind (see 'form')");
    _ = sb.AppendLine("  flattenForm {fields?}              - merge the form into the page content and remove the fields");
    _ = sb.AppendLine("The batch is atomic: one invalid operation refuses the whole batch, nothing is saved.");
    _ = sb.AppendLine("Output: [pdf manipulated <path>] operations=<n> pages=<n> version v<newtoken>.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("create markdown subset: '# '/## '/### ' headings; '- ' bullets; '1. '/'2. ' numbered; **bold**; *italic*; '| a | b |' consecutive lines = table ('|---|' separator rows ignored); blank line = paragraph break; anything else = literal paragraph.");
    _ = sb.AppendLine("Output: [pdf created <path>] pages=<n> version v<token>.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("render output: one PNG attachment per requested page plus the line [pdf render <path>] pages=<n> dpi=<dpi>; without image input the pages are listed only.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("form output (verbatim contract):");
    _ = sb.AppendLine("[pdf form <path> v<version> fields=<n> pages=<total>]");
    _ = sb.AppendLine("  <kind> '<name>' page=<n> value=\"<current>\" options=[\"<export>\", ...]");
    _ = sb.AppendLine("version v<length>:<ticks>");
    _ = sb.AppendLine("<kind> is one of text, checkbox, radio, choice, button, signature, unsupported. 'options' appears only where the field offers a fixed set (choice, radio). A document with no form prints 'no form fields'.");
    _ = sb.AppendLine("Pass the 'version v<...>' line back as expectedVersion to fill or flatten.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("fillFormField value by kind: text = the text; checkbox and radio = true or false (radio also accepts an option's export value); choice = one of the field's options. A push button or signature field cannot be filled. An unknown field fails FormFieldNotFound; a value the kind cannot hold fails FormFieldTypeMismatch.");
    _ = sb.AppendLine("flattenForm with no 'fields' flattens every field: the values are merged into the page content and the interactive fields are removed, so the result renders identically and can no longer be edited.");
    _ = sb.AppendLine();
    _ = sb.AppendLine("Errors: Error [NotAPdf|EncryptedPdf|CorruptPdf|OversizePdf|PageNotFound|InvalidPageRange|OutputExists|StaleDocument|MarkdownInvalid|FileNotFound|FormFieldNotFound|FormFieldTypeMismatch|InvalidParameterValue|MissingParameter|UnknownParameter|PathOutsideWorkspace].");
    return sb.ToString();
  }

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<PdfToolInput> parsed = PdfToolInput.FromArguments(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    PdfToolInput args = parsed.Value;
    // Action-scoped resolution (issue #96): reading and rendering may reach a
    // read-only trusted root; create and manipulate keep the strict resolver.
    Result<string> resolved = ActionScopedPathResolver.Resolve(
        _resolver, args.Path!, input.JsonArguments, ReadOnlyActions);
    if (!resolved.IsSuccess)
    {
      return Task.FromResult(Err(resolved.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
        ? Task.FromResult(Err(budget.Error))
        : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => DispatchAsync(args, resolved.Value, token), ct);
  }

  private async Task<ToolResult> DispatchAsync(PdfToolInput args, string path, CancellationToken ct)
  {
    return args.Action switch
    {
      "read" => await ReadAsync(args, path, ct).ConfigureAwait(false),
      "render" => await RenderAsync(args, path, ct).ConfigureAwait(false),
      "create" => await CreateAsync(args, path, ct).ConfigureAwait(false),
      "manipulate" => await ManipulateAsync(args, path, ct).ConfigureAwait(false),
      "form" => await FormAsync(path, ct).ConfigureAwait(false),
      _ => new ToolResult($"Error [InvalidParameterValue]: unknown action '{args.Action}'.", true),
    };
  }

  private async Task<ToolResult> ReadAsync(PdfToolInput args, string path, CancellationToken ct)
  {
    Result<PdfSnapshot> read = await _pdf.ReadAsync(path, args.Offset, args.MaxPages, ct).ConfigureAwait(false);
    if (!read.IsSuccess)
    {
      return Err(read.Error);
    }

    PdfSnapshot snap = read.Value;
    int first = args.Offset + 1;
    int last = args.Offset + snap.Pages.Count;
    _ledger?.Record(path, snap.Version, first, last);

    StringBuilder sb = new();
    _ = sb.AppendLine(CultureInfo.InvariantCulture,
        $"[pdf {path} v{snap.Version.Token} pages {first}-{last} of {snap.TotalPages}]");
    foreach (PdfPageText page in snap.Pages)
    {
      _ = sb.AppendLine(CultureInfo.InvariantCulture,
          $"--- page {page.PageNumber} ({FormatPoints(page.WidthPt)}x{FormatPoints(page.HeightPt)}pt) ---");
      foreach (string line in page.Lines)
      {
        _ = sb.AppendLine(line);
      }
    }

    _ = sb.Append(CultureInfo.InvariantCulture, $"version {FileVersionToken.Encode(snap.Version)}");
    return new ToolResult(sb.ToString(), false);
  }

  private async Task<ToolResult> RenderAsync(PdfToolInput args, string path, CancellationToken ct)
  {
    Result<PdfRenderOutcome> rendered = await _pdf.RenderAsync(path, args.Pages!, args.Dpi, ct).ConfigureAwait(false);
    if (!rendered.IsSuccess)
    {
      return Err(rendered.Error);
    }

    PdfRenderOutcome outcome = rendered.Value;
    string header = string.Create(CultureInfo.InvariantCulture,
        $"[pdf render {path}] pages={outcome.RenderedPages.Count} dpi={args.Dpi}");
    bool visionAccepts = _vision?.AcceptsImages == true;

    if (!visionAccepts)
    {
      StringBuilder listed = new();
      _ = listed.AppendLine(header);
      foreach (int page in outcome.RenderedPages)
      {
        _ = listed.AppendLine(CultureInfo.InvariantCulture,
            $"p{page} listed-only (ImageNotAttachable: model has no image input)");
      }

      listed.Length -= Environment.NewLine.Length;
      return new ToolResult(listed.ToString(), false);
    }

    List<ToolResultImage> images = [];
    foreach (PdfPageImage image in outcome.Images)
    {
      images.Add(new ToolResultImage(PngMediaType, Convert.ToBase64String(image.PngBytes)));
    }

    return new ToolResult(header, false, Images: images.Count > 0 ? images : null);
  }

  private async Task<ToolResult> FormAsync(string path, CancellationToken ct)
  {
    Result<PdfFormSnapshot> read = await _pdf.ReadFormAsync(path, ct).ConfigureAwait(false);
    if (!read.IsSuccess)
    {
      return Err(read.Error);
    }

    PdfFormSnapshot snap = read.Value;
    _ledger?.Record(path, snap.Version, 1, snap.TotalPages);

    StringBuilder sb = new();
    _ = sb.AppendLine(CultureInfo.InvariantCulture,
        $"[pdf form {path} v{snap.Version.Token} fields={snap.Fields.Count} pages={snap.TotalPages}]");
    if (snap.Fields.Count == 0)
    {
      _ = sb.AppendLine("no form fields (the document carries no AcroForm)");
    }

    foreach (PdfFormField field in snap.Fields)
    {
      _ = sb.AppendLine(FieldLine(field));
    }

    _ = sb.Append(CultureInfo.InvariantCulture, $"version {FileVersionToken.Encode(snap.Version)}");
    return new ToolResult(sb.ToString(), false);
  }

  /// <summary>One field's contract line: name, kind, page, current value, and - for a
  ///     choice field - the export values it accepts.</summary>
  private static string FieldLine(PdfFormField field)
  {
    string page = field.Page is { } number ? number.ToString(CultureInfo.InvariantCulture) : "-";
    StringBuilder line = new();
    _ = line.Append(CultureInfo.InvariantCulture,
        $"  {KindName(field.Kind)} '{field.Name}' page={page} value={Quote(field.Value)}");
    if (field.Options.Count > 0)
    {
      _ = line.Append(CultureInfo.InvariantCulture,
          $" options=[{string.Join(", ", field.Options.Select(Quote))}]");
    }

    return line.ToString();
  }

  /// <summary>The lower-case kind name the contract prints.</summary>
  private static string KindName(PdfFormFieldKind kind) => kind switch
  {
    PdfFormFieldKind.Text => "text",
    PdfFormFieldKind.Checkbox => "checkbox",
    PdfFormFieldKind.Radio => "radio",
    PdfFormFieldKind.Choice => "choice",
    PdfFormFieldKind.Button => "button",
    PdfFormFieldKind.Signature => "signature",
    PdfFormFieldKind.Unsupported => "unsupported",
    _ => "unsupported",
  };

  /// <summary>Quotes a value so an empty one is visible in the listing.</summary>
  private static string Quote(string value) => "\"" + value + "\"";

  private async Task<ToolResult> CreateAsync(PdfToolInput args, string path, CancellationToken ct)
  {
    Result<PdfSaveOutcome> created = await _pdf.CreateAsync(path, args.Markdown!, ct).ConfigureAwait(false);
    return !created.IsSuccess
        ? Err(created.Error)
        : Ok($"[pdf created {path}] pages={created.Value.PageCount} version {FileVersionToken.Encode(created.Value.Version)}");
  }

  private async Task<ToolResult> ManipulateAsync(PdfToolInput args, string path, CancellationToken ct)
  {
    Result<PdfEditOutcome> edited = await _pdf
        .ManipulateAsync(path, args.ExpectedVersion!, args.Operations, ct).ConfigureAwait(false);
    return !edited.IsSuccess
        ? Err(edited.Error)
        : Ok($"[pdf manipulated {path}] operations={edited.Value.OperationsApplied} pages={edited.Value.PageCount} version {FileVersionToken.Encode(edited.Value.NewVersion)}");
  }

  /// <summary>Formats a point dimension without trailing zeros (612x792pt).</summary>
  private static string FormatPoints(double value)
      => value.ToString("0.###", CultureInfo.InvariantCulture);

  private static ToolResult Ok(string content) => new(content, false);

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
