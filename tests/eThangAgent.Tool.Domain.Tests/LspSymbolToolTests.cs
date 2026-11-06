using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Contract tests for the 'lsp' symbol navigation dispatch tool (issue #357):
/// strict input parsing, symbol-name addressing, deterministic multi-occurrence,
/// unsupported labels, and Unavailable honesty. The seam is scripted.</summary>
public class LspSymbolToolTests
{
  private sealed class ScriptedSymbolAccess : ILspSymbolAccess
  {
    public LspSymbolOutcome Next { get; set; } = new LspSymbolOutcome.Ready([]);
    public List<LspSymbolCommand> Commands { get; } = [];

    public Task<LspSymbolOutcome> ExecuteAsync(LspSymbolCommand command, CancellationToken ct = default)
    {
      Commands.Add(command);
      return Task.FromResult(Next);
    }
  }

  private static string Args(string inner) =>
      "{" + "\"timeoutSeconds\":120" + (inner.Length == 0 ? "" : "," + inner) + "}";

  private static async Task<ToolResult> Call(ScriptedSymbolAccess access, string inner)
  {
    LspSymbolTool tool = new(new WorkspacePathResolver("C:\\ws"), access);
    return await tool.ExecuteAsync(new RawToolInput("lsp", Args(inner)),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  [Fact]
  public void Definition_NameAndRequired()
  {
    LspSymbolTool tool = new(new WorkspacePathResolver("C:\\ws"), new ScriptedSymbolAccess());
    Assert.Equal("lsp", tool.Definition.Name);
    Assert.Equal(["timeoutSeconds", "action"], tool.Definition.RequiredParameters);
  }

  [Fact]
  public void Definition_DescriptionDocumentsActionsAndLabels()
  {
    string d = new LspSymbolTool(new WorkspacePathResolver("C:\\ws"), new ScriptedSymbolAccess()).Definition.Description;
    foreach (string a in new[] { "definition", "references", "hover", "document-symbols", "workspace-symbols", "implementation" })
    {
      Assert.Contains(a, d, StringComparison.Ordinal);
    }
    foreach (string k in new[] { "path", "symbolName", "occurrence" })
    {
      Assert.Contains(k, d, StringComparison.Ordinal);
    }
    foreach (string label in new[] { "Ready", "Starting", "Stale", "Degraded", "Unavailable", "Unsupported" })
    {
      Assert.Contains(label, d, StringComparison.Ordinal);
    }
    Assert.Contains("[lsp]", d, StringComparison.Ordinal);
    Assert.Contains("lazily", d, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingAction_Fails_InvalidAction()
  {
    ToolResult r = await Call(new ScriptedSymbolAccess(), "").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("Error [InvalidAction]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownAction_Fails_InvalidAction()
  {
    ToolResult r = await Call(new ScriptedSymbolAccess(), "\"action\": \"notAnAction\"").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("Error [InvalidAction]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task DefinitionWithoutPath_Fails_MissingParameter()
  {
    ToolResult r = await Call(new ScriptedSymbolAccess(), "\"action\": \"definition\", \"symbolName\": \"Foo\"").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("Error [MissingParameter]: Missing required parameter 'path'.", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task DefinitionWithoutSymbolName_Fails_MissingParameter()
  {
    ToolResult r = await Call(new ScriptedSymbolAccess(), "\"action\": \"definition\", \"path\": \"A.cs\"").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("Error [MissingParameter]: Missing required parameter 'symbolName'.", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task References_Dispatches_ReferencesCommand_WithResolvedPath()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Ready([new LspSymbolSite("C:\\ws\\A.cs", 1, 5, "Foo")]) };
    ToolResult r = await Call(access, "\"action\": \"references\", \"path\": \"A.cs\", \"symbolName\": \"Foo\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    LspSymbolCommand.References cmd = Assert.IsType<LspSymbolCommand.References>(Assert.Single(access.Commands));
    Assert.Equal("C:\\ws\\A.cs", cmd.Path);
    Assert.Equal("Foo", cmd.SymbolName);
    Assert.Equal(0, cmd.Occurrence);
    Assert.Contains("[lsp]", r.Content, StringComparison.Ordinal);
    Assert.Contains("Ready", r.Content, StringComparison.Ordinal);
    Assert.Contains("A.cs:2:6", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Definition_MultiOccurrence_NamesOccurrenceUsed()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Ready([new LspSymbolSite("C:\\ws\\A.cs", 3, 2, "Foo")], OccurrenceUsed: 1, TotalOccurrences: 2) };
    ToolResult r = await Call(access, "\"action\": \"definition\", \"path\": \"A.cs\", \"symbolName\": \"Foo\", \"occurrence\": 1").ConfigureAwait(true);
    Assert.False(r.IsError);
    LspSymbolCommand.Definition cmd = Assert.IsType<LspSymbolCommand.Definition>(Assert.Single(access.Commands));
    Assert.Equal(1, cmd.Occurrence);
    Assert.Contains("occurrence 1", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Unsupported_Renders_UnSupportedLabel_NotEmpty()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Unsupported("capability not advertised for textDocument/definition") };
    ToolResult r = await Call(access, "\"action\": \"definition\", \"path\": \"A.cs\", \"symbolName\": \"Foo\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    Assert.Contains("Unsupported", r.Content, StringComparison.Ordinal);
    Assert.Contains("capability not advertised", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ReferencedOnlySymbol_Returns_Unsupported_NotGuessed()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Unsupported("symbol 'Bar' is only referenced, not defined, in A.cs; position cannot be resolved") };
    ToolResult r = await Call(access, "\"action\": \"references\", \"path\": \"A.cs\", \"symbolName\": \"Bar\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    Assert.Contains("Unsupported", r.Content, StringComparison.Ordinal);
    Assert.Contains("only referenced", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Unavailable_Renders_UnavailableLabel()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Unavailable("no language server for this workspace") };
    ToolResult r = await Call(access, "\"action\": \"hover\", \"path\": \"A.cs\", \"symbolName\": \"Foo\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    Assert.Contains("Unavailable", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WorkspaceSymbols_Dispatches_WithoutPath()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Ready([new LspSymbolSite("C:\\ws\\B.cs", 0, 0, "Baz")]) };
    ToolResult r = await Call(access, "\"action\": \"workspace-symbols\", \"symbolName\": \"Baz\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    LspSymbolCommand.WorkspaceSymbols cmd = Assert.IsType<LspSymbolCommand.WorkspaceSymbols>(Assert.Single(access.Commands));
    Assert.Equal("Baz", cmd.SymbolName);
    Assert.Contains("B.cs:1:1", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task DocumentSymbols_Dispatches_WithPathOnly()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Ready([new LspSymbolSite("C:\\ws\\A.cs", 0, 0, "Foo")]) };
    ToolResult r = await Call(access, "\"action\": \"document-symbols\", \"path\": \"A.cs\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    LspSymbolCommand.DocumentSymbols cmd = Assert.IsType<LspSymbolCommand.DocumentSymbols>(Assert.Single(access.Commands));
    Assert.Equal("C:\\ws\\A.cs", cmd.Path);
  }

  [Fact]
  public async Task Hover_Dispatches_HoverCommand()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Ready([new LspSymbolSite("C:\\ws\\A.cs", 2, 4, "Foo")]) };
    ToolResult r = await Call(access, "\"action\": \"hover\", \"path\": \"A.cs\", \"symbolName\": \"Foo\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    _ = Assert.IsType<LspSymbolCommand.Hover>(Assert.Single(access.Commands));
  }

  [Fact]
  public async Task Implementation_Dispatches_ImplementationCommand()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Ready([new LspSymbolSite("C:\\ws\\A.cs", 2, 4, "Foo")]) };
    ToolResult r = await Call(access, "\"action\": \"implementation\", \"path\": \"A.cs\", \"symbolName\": \"Foo\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    _ = Assert.IsType<LspSymbolCommand.Implementation>(Assert.Single(access.Commands));
  }

  [Fact]
  public void RootedAt_ReRoots_PathResolution()
  {
    LspSymbolTool tool = new(new WorkspacePathResolver("C:\\ws"), new ScriptedSymbolAccess());
    ITool rooted = tool.RootedAt("C:\\other");
    Assert.Equal("lsp", rooted.Definition.Name);
  }

  [Fact]
  public async Task DocumentSymbols_Annotation_ReportsCount_NotOccurrence()
  {
    ScriptedSymbolAccess access = new()
    {
      Next = new LspSymbolOutcome.Ready(
          [new LspSymbolSite("C:\\ws\\A.cs", 0, 5, "Foo"), new LspSymbolSite("C:\\ws\\A.cs", 3, 2, "Bar")],
          0, 2),
    };
    ToolResult result = await Call(access, "\"action\":\"document-symbols\",\"path\":\"A.cs\"");
    Assert.Contains("2 symbols", result.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("occurrence", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WorkspaceSymbols_Annotation_ReportsCount_NotOccurrence()
  {
    ScriptedSymbolAccess access = new()
    {
      Next = new LspSymbolOutcome.Ready([new LspSymbolSite("C:\\ws\\A.cs", 0, 5, "Foo")], 0, 1),
    };
    ToolResult result = await Call(access, "\"action\":\"workspace-symbols\",\"symbolName\":\"Foo\"");
    Assert.Contains("1 symbol", result.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("occurrence", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WorkspaceLoad_IsAnAction_WithNoPathOrSymbol()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Ready([], 0, 1) };
    ToolResult result = await Call(access, "\"action\":\"workspace-load\"");
    Assert.False(result.IsError);
    _ = Assert.IsType<LspSymbolCommand.WorkspaceLoad>(Assert.Single(access.Commands));
  }

  [Fact]
  public async Task WorkspaceLoad_RejectsPathAndSymbolName()
  {
    ScriptedSymbolAccess access = new();
    ToolResult result = await Call(access, "\"action\":\"workspace-load\",\"path\":\"A.cs\"");
    Assert.True(result.IsError);
    Assert.Contains("UnknownParameter", result.Content, StringComparison.Ordinal);
    Assert.Empty(access.Commands);
  }

  [Fact]
  public void Definition_DescriptionDocumentsWorkspaceLoad()
  {
    string d = new LspSymbolTool(new WorkspacePathResolver("C:\\ws"), new ScriptedSymbolAccess()).Definition.Description;
    Assert.Contains("workspace-load", d, StringComparison.Ordinal);
  }

  private sealed class RecordingApplier : ILspRenameApplier
  {
    public int Calls { get; private set; }

    public IReadOnlyList<LspRenameEdit> Edits { get; private set; } = [];

    public Task<Result<LspRenameApplied>> ApplyAsync(IReadOnlyList<LspRenameEdit> edits, CancellationToken ct = default)
    {
      Calls++;
      Edits = edits;
      IReadOnlyList<string> files = [.. edits.Select(e => e.Path).Distinct(StringComparer.Ordinal)];
      return Task.FromResult(Result.Success(new LspRenameApplied(files, edits.Count)));
    }
  }

  private static async Task<ToolResult> CallWithApplier(ScriptedSymbolAccess access, RecordingApplier applier, string inner)
  {
    LspSymbolTool tool = new(new WorkspacePathResolver("C:\\ws"), access, applier);
    return await tool.ExecuteAsync(new RawToolInput("lsp", Args(inner)),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  [Fact]
  public async Task Rename_WithoutNewName_Fails_MissingParameter()
  {
    ToolResult r = await Call(new ScriptedSymbolAccess(), "\"action\": \"rename\", \"path\": \"A.cs\", \"symbolName\": \"Foo\"").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("Error [MissingParameter]:", r.Content, StringComparison.Ordinal);
    Assert.Contains("newName", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Rename_DefaultReportsWithoutApplying()
  {
    ScriptedSymbolAccess access = new()
    {
      Next = new LspSymbolOutcome.Renamed([new LspRenameEdit(@"C:\ws\A.cs", 3, 10, 3, 13, "Bar")], "Foo", Applied: false),
    };
    RecordingApplier applier = new();
    ToolResult r = await CallWithApplier(access, applier, "\"action\": \"rename\", \"path\": \"A.cs\", \"symbolName\": \"Foo\", \"newName\": \"Bar\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    Assert.Contains("1 edit", r.Content, StringComparison.Ordinal);
    Assert.Contains("A.cs:4:11", r.Content, StringComparison.Ordinal);
    Assert.Contains("not applied", r.Content, StringComparison.Ordinal);
    Assert.Equal(0, applier.Calls);
    LspSymbolCommand.Rename cmd = Assert.IsType<LspSymbolCommand.Rename>(Assert.Single(access.Commands));
    Assert.Equal("C:\\ws\\A.cs", cmd.Path);
    Assert.Equal("Foo", cmd.SymbolName);
    Assert.Equal("Bar", cmd.NewName);
    Assert.Equal(0, cmd.Occurrence);
  }

  [Fact]
  public async Task Rename_ApplyTrue_InvokesTheApplier()
  {
    ScriptedSymbolAccess access = new()
    {
      Next = new LspSymbolOutcome.Renamed([new LspRenameEdit(@"C:\ws\A.cs", 3, 10, 3, 13, "Bar")], "Foo", Applied: false),
    };
    RecordingApplier applier = new();
    ToolResult r = await CallWithApplier(access, applier,
        "\"action\": \"rename\", \"path\": \"A.cs\", \"symbolName\": \"Foo\", \"newName\": \"Bar\", \"apply\": true").ConfigureAwait(true);
    Assert.False(r.IsError);
    Assert.Equal(1, applier.Calls);
    _ = Assert.Single(applier.Edits);
    Assert.Contains("applied", r.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("not applied", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Rename_ApplyTrue_WithoutApplier_Fails_Unavailable()
  {
    ScriptedSymbolAccess access = new()
    {
      Next = new LspSymbolOutcome.Renamed([new LspRenameEdit(@"C:\ws\A.cs", 3, 10, 3, 13, "Bar")], "Foo", Applied: false),
    };
    ToolResult r = await Call(access,
        "\"action\": \"rename\", \"path\": \"A.cs\", \"symbolName\": \"Foo\", \"newName\": \"Bar\", \"apply\": true").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("Error [Unavailable]:", r.Content, StringComparison.Ordinal);
    Assert.Contains("apply", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Rename_ApplyTrue_ApplierFailure_SurfacesTheError()
  {
    ScriptedSymbolAccess access = new()
    {
      Next = new LspSymbolOutcome.Renamed([new LspRenameEdit(@"C:\ws\A.cs", 3, 10, 3, 13, "Bar")], "Foo", Applied: false),
    };
    LspSymbolTool tool = new(new WorkspacePathResolver("C:\\ws"), access, new FailingApplier());
    ToolResult r = await tool.ExecuteAsync(new RawToolInput("lsp",
        Args("\"action\": \"rename\", \"path\": \"A.cs\", \"symbolName\": \"Foo\", \"newName\": \"Bar\", \"apply\": true")),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("PathOutsideWorkspace", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Rename_ReportsTheEditCountAndEveryEdit()
  {
    ScriptedSymbolAccess access = new()
    {
      Next = new LspSymbolOutcome.Renamed(
          [
            new LspRenameEdit(@"C:\ws\A.cs", 0, 4, 0, 7, "Bar"),
            new LspRenameEdit(@"C:\ws\B.cs", 9, 2, 9, 5, "Bar"),
          ],
          "Foo",
          Applied: false),
    };
    ToolResult r = await Call(access, "\"action\": \"rename\", \"path\": \"A.cs\", \"symbolName\": \"Foo\", \"newName\": \"Bar\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    Assert.Contains("2 edits", r.Content, StringComparison.Ordinal);
    Assert.Contains("A.cs:1:5", r.Content, StringComparison.Ordinal);
    Assert.Contains("B.cs:10:3", r.Content, StringComparison.Ordinal);
    Assert.Contains("Bar", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Rename_NoEdits_IsReadyAndSaysSo()
  {
    ScriptedSymbolAccess access = new()
    {
      Next = new LspSymbolOutcome.Renamed([], "Foo", Applied: false, Message: "the server returned no edits"),
    };
    ToolResult r = await Call(access, "\"action\": \"rename\", \"path\": \"A.cs\", \"symbolName\": \"Foo\", \"newName\": \"Bar\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    Assert.Contains("0 edits", r.Content, StringComparison.Ordinal);
    Assert.Contains("no edits", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Rename_Unsupported_Renders_UnsupportedLabel()
  {
    ScriptedSymbolAccess access = new()
    {
      Next = new LspSymbolOutcome.Unsupported("capability not advertised for textDocument/rename"),
    };
    ToolResult r = await Call(access, "\"action\": \"rename\", \"path\": \"A.cs\", \"symbolName\": \"Foo\", \"newName\": \"Bar\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    Assert.Contains("Unsupported", r.Content, StringComparison.Ordinal);
    Assert.Contains("not advertised", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Rename_RejectsUnknownParameter()
  {
    ScriptedSymbolAccess access = new();
    ToolResult r = await Call(access, "\"action\": \"rename\", \"path\": \"A.cs\", \"symbolName\": \"Foo\", \"newName\": \"Bar\", \"line\": 3").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("UnknownParameter", r.Content, StringComparison.Ordinal);
    Assert.Empty(access.Commands);
  }

  [Fact]
  public void Definition_DescriptionDocumentsRename()
  {
    string d = new LspSymbolTool(new WorkspacePathResolver("C:\\ws"), new ScriptedSymbolAccess()).Definition.Description;
    Assert.Contains("rename", d, StringComparison.Ordinal);
    Assert.Contains("newName", d, StringComparison.Ordinal);
    Assert.Contains("apply", d, StringComparison.Ordinal);
    Assert.Contains("not applied", d, StringComparison.Ordinal);
    Assert.Contains("file renames", d, StringComparison.Ordinal);
  }

  private sealed class FailingApplier : ILspRenameApplier
  {
    public Task<Result<LspRenameApplied>> ApplyAsync(IReadOnlyList<LspRenameEdit> edits, CancellationToken ct = default)
      => Task.FromResult(Result.Failure<LspRenameApplied>(new DomainError("PathOutsideWorkspace",
          "'C:\\other\\A.cs' is outside the workspace.")));
  }
}
