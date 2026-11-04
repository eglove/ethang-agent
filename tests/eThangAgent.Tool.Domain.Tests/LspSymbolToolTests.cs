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
    ToolResult r = await Call(new ScriptedSymbolAccess(), "\"action\": \"rename\"").ConfigureAwait(true);
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
}
