using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>Drives the REAL bundled Roslyn server through the production daemon
/// executable and the production pipe protocol to prove symbol navigation resolves
/// members that a server reports with their signature attached. Opt-in by trait - it
/// needs a built daemon and a machine where Roslyn can load the workspace - and out of
/// the default suite. Run it with --filter-trait "Category=OptIn" and
/// ETHANG_LSP_REAL_SERVER=1.</summary>
[Trait("Category", "OptIn")]
[Trait("Category", "RealLspServer")]
[Trait("Category", "Process")]
public class LspRealServerSymbolTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private const string Source =
      "namespace Probe;\n" +
      "\n" +
      "public sealed class Widget\n" +
      "{\n" +
      "  private int _count;\n" +
      "\n" +
      "  public int Bump(int by)\n" +
      "  {\n" +
      "    _count += by;\n" +
      "    return _count;\n" +
      "  }\n" +
      "}\n" +
      "\n" +
      "// A primary constructor makes the server report the type and its constructor\n" +
      "// over one range, which is the shape a reference answer must not duplicate.\n" +
      "public sealed class Holder(int seed)\n" +
      "{\n" +
      "  public int Seed => seed;\n" +
      "}\n";

  [Fact]
  public async Task MemberSymbols_ResolveAgainstTheRealServer()
  {
    OptInTest.SkipUnlessEnabled("ETHANG_LSP_REAL_SERVER",
        "with a built daemon and a resolvable Roslyn server.");

    string daemonExe = LspDaemonExeLocator.CreateDefault().Resolve();
    Assert.True(File.Exists(daemonExe), "the language-server daemon must be built first: " + daemonExe);

    string root = TestTempRoot.CreateDirectory("ethang-lsp-symbol").FullName;
    string workspaceId = "real-sym-" + Guid.NewGuid().ToString("N");
    string pipeName = LspPipeNames.ForWorkspaceId(workspaceId);
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "probe.csproj"),
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", Ct).ConfigureAwait(true);
      await File.WriteAllTextAsync(Path.Combine(root, "probe.slnx"),
          "<Solution>\n  <Project Path=\"probe.csproj\" />\n</Solution>\n", Ct).ConfigureAwait(true);
      string file = Path.Combine(root, "Widget.cs");
      await File.WriteAllTextAsync(file, Source, Ct).ConfigureAwait(true);

      System.Diagnostics.ProcessStartInfo psi = new(daemonExe)
      {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      };
      psi.ArgumentList.Add(pipeName);
      psi.ArgumentList.Add(workspaceId);
      psi.ArgumentList.Add(root);
      using System.Diagnostics.Process daemon = System.Diagnostics.Process.Start(psi)!;
      try
      {
        _ = daemon.StandardOutput.ReadToEndAsync(Ct);
        _ = daemon.StandardError.ReadToEndAsync(Ct);

        LspPipeClient client = await ConnectAsync(pipeName, workspaceId).ConfigureAwait(true);
        string uri = LspUriMapper.ToUri(file);

        // A cold workspace reports Starting until its load settles, so the load is
        // requested first: an unloaded server answers with declarations only.
        LspSymbolResult loaded = await client.SymbolAsync("workspace-load", string.Empty, string.Empty, 0, 1, newName: string.Empty, Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Ready, loaded.State);

        LspSymbolResult document = await client.SymbolAsync("document-symbols", uri, string.Empty, 0, 1, newName: string.Empty, Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Ready, document.State);
        Assert.NotEmpty(document.Sites);

        // Every document symbol names its own file: a site rendered without a path is
        // unactionable to the caller.
        Assert.All(document.Sites, site => Assert.Equal(uri, site.Uri));

        // A member the server reports as 'Bump(int) : int' must still resolve by its
        // declared name - the defect this test exists to prevent.
        LspSymbolResult definition = await client.SymbolAsync("definition", uri, "Bump", 0, 1, newName: string.Empty, Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Ready, definition.State);
        Assert.NotEmpty(definition.Sites);

        LspSymbolResult hover = await client.SymbolAsync("hover", uri, "Bump", 0, 1, newName: string.Empty, Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Ready, hover.State);
        Assert.False(string.IsNullOrWhiteSpace(hover.Message), "hover must carry the server's contents, not a bare position");

        // A hover response carries no location, so its site is built from the request:
        // it must still name the file that was asked about.
        Assert.All(hover.Sites, site => Assert.Equal(uri, site.Uri));

        // A server reports a type and the primary constructor it declares over one
        // range; the reference answer must not list that declaration twice.
        LspSymbolResult references = await client.SymbolAsync("references", uri, "Holder", 0, 1, newName: string.Empty, Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Ready, references.State);
        Assert.Equal(references.Sites.Count, references.Sites.Distinct().Count());

        LspSymbolResult field = await client.SymbolAsync("definition", uri, "_count", 0, 1, newName: string.Empty, Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Ready, field.State);
        Assert.NotEmpty(field.Sites);

        client.Dispose();
      }
      finally
      {
        if (!daemon.HasExited)
        {
          daemon.Kill(entireProcessTree: true);
        }
      }
    }
    finally
    {
      DeleteWorkspace(root);
    }
  }

  /// <summary>Applies a document's edits to its text, last position first so an earlier
  /// edit never shifts a later one. Line and character are 0-based, as the protocol
  /// reports them.</summary>
  private static string ApplyEdits(string text, IReadOnlyList<LspTextEdit> edits)
  {
    string[] lines = text.Split('\n');
    List<LspTextEdit> ordered = [.. edits.OrderByDescending(edit => edit.StartLine).ThenByDescending(edit => edit.StartCharacter)];
    foreach (LspTextEdit edit in ordered)
    {
      int start = Offset(lines, edit.StartLine, edit.StartCharacter);
      int end = Offset(lines, edit.EndLine, edit.EndCharacter);
      text = string.Concat(text.AsSpan(0, start), edit.NewText, text.AsSpan(end));
      lines = text.Split('\n');
    }

    return text;
  }

  private static bool IsSameFile(string uri, string path)
    => string.Equals(LspUriMapper.FromUri(uri), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

  private static int Offset(string[] lines, int line, int character)
  {
    int offset = 0;
    for (int i = 0; i < line; i++)
    {
      offset += lines[i].Length + 1;
    }

    return offset + character;
  }

  private static async Task<LspPipeClient> ConnectAsync(string pipeName, string workspaceId)
  {
    for (int attempt = 0; attempt < 600; attempt++)
    {
      try
      {
        return await LspPipeClient.ConnectAsync(pipeName, workspaceId, "app-1", Ct).ConfigureAwait(true);
      }
      catch (Exception ex) when (ex is TimeoutException or IOException)
      {
        await Task.Delay(250, Ct).ConfigureAwait(true);
      }
    }

    throw new InvalidOperationException("the language-server daemon never accepted a connection");
  }

  private static void DeleteWorkspace(string root)
  {
    for (int attempt = 0; attempt < 20; attempt++)
    {
      try
      {
        Directory.Delete(root, recursive: true);
        return;
      }
      catch (IOException)
      {
        Thread.Sleep(50);
      }
      catch (UnauthorizedAccessException)
      {
        Thread.Sleep(50);
      }
    }
  }

  [Fact]
  public async Task ColdWorkspace_ReportsStarting_ThenWorkspaceLoadAnswers()
  {
    OptInTest.SkipUnlessEnabled("ETHANG_LSP_REAL_SERVER",
        "with a built daemon and a resolvable Roslyn server.");

    string daemonExe = LspDaemonExeLocator.CreateDefault().Resolve();
    Assert.True(File.Exists(daemonExe), "the language-server daemon must be built first: " + daemonExe);

    string root = TestTempRoot.CreateDirectory("ethang-lsp-cold").FullName;
    string workspaceId = "real-cold-" + Guid.NewGuid().ToString("N");
    string pipeName = LspPipeNames.ForWorkspaceId(workspaceId);
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "probe.csproj"),
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", Ct).ConfigureAwait(true);
      await File.WriteAllTextAsync(Path.Combine(root, "probe.slnx"),
          "<Solution>\n  <Project Path=\"probe.csproj\" />\n</Solution>\n", Ct).ConfigureAwait(true);
      string file = Path.Combine(root, "Widget.cs");
      await File.WriteAllTextAsync(file, Source, Ct).ConfigureAwait(true);

      System.Diagnostics.ProcessStartInfo psi = new(daemonExe)
      {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      };
      psi.ArgumentList.Add(pipeName);
      psi.ArgumentList.Add(workspaceId);
      psi.ArgumentList.Add(root);
      using System.Diagnostics.Process daemon = System.Diagnostics.Process.Start(psi)!;
      try
      {
        _ = daemon.StandardOutput.ReadToEndAsync(Ct);
        _ = daemon.StandardError.ReadToEndAsync(Ct);

        LspPipeClient client = await ConnectAsync(pipeName, workspaceId).ConfigureAwait(true);
        string uri = LspUriMapper.ToUri(file);

        // A workspace the server has not loaded has no compilation behind it. A query
        // answered from the file-based project system reports the type's own declaration
        // and none of its cross-file uses - an empty Ready that reads as truth. The state
        // must say Starting instead, whatever the site list happens to contain.
        LspSymbolResult cold = await client.SymbolAsync("references", uri, "Widget", 0, 1, newName: string.Empty, Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Starting, cold.State);
        Assert.Empty(cold.Sites);

        // workspace-load is the action that waits for the load, so the query after it has
        // a compilation to answer from.
        _ = await client.SymbolAsync("workspace-load", string.Empty, string.Empty, 0, 1, newName: string.Empty, Ct).ConfigureAwait(true);

        LspSymbolResult warm = await client.SymbolAsync("references", uri, "Widget", 0, 1, newName: string.Empty, Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Ready, warm.State);
        Assert.NotEmpty(warm.Sites);

        client.Dispose();
      }
      finally
      {
        if (!daemon.HasExited)
        {
          daemon.Kill(entireProcessTree: true);
        }
      }
    }
    finally
    {
      DeleteWorkspace(root);
    }
  }

  [Fact]
  public async Task RealServer_Rename_ReturnsEditsCoveringEveryUsage()
  {
    OptInTest.SkipUnlessEnabled("ETHANG_LSP_REAL_SERVER",
        "with a built daemon and a resolvable Roslyn server.");

    string daemonExe = LspDaemonExeLocator.CreateDefault().Resolve();
    Assert.True(File.Exists(daemonExe), "the language-server daemon must be built first: " + daemonExe);

    string root = TestTempRoot.CreateDirectory("ethang-lsp-rename").FullName;
    string workspaceId = "real-rename-" + Guid.NewGuid().ToString("N");
    string pipeName = LspPipeNames.ForWorkspaceId(workspaceId);
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "probe.csproj"),
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", Ct).ConfigureAwait(true);
      await File.WriteAllTextAsync(Path.Combine(root, "probe.slnx"),
          "<Solution>\n  <Project Path=\"probe.csproj\" />\n</Solution>\n", Ct).ConfigureAwait(true);

      // The declaration and its uses live in two files, so an edit set that covers only
      // the declaration is visibly incomplete.
      string declarationFile = Path.Combine(root, "Widget.cs");
      await File.WriteAllTextAsync(declarationFile,
          "namespace Probe;\n\npublic class Widget\n{\n}\n", Ct).ConfigureAwait(true);
      string usageFile = Path.Combine(root, "Uses.cs");
      await File.WriteAllTextAsync(usageFile,
          "namespace Probe;\n\npublic class Uses\n{\n  public Widget W { get; } = new Widget();\n}\n", Ct).ConfigureAwait(true);

      System.Diagnostics.ProcessStartInfo psi = new(daemonExe)
      {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      };
      psi.ArgumentList.Add(pipeName);
      psi.ArgumentList.Add(workspaceId);
      psi.ArgumentList.Add(root);
      using System.Diagnostics.Process daemon = System.Diagnostics.Process.Start(psi)!;
      try
      {
        _ = daemon.StandardOutput.ReadToEndAsync(Ct);
        _ = daemon.StandardError.ReadToEndAsync(Ct);

        LspPipeClient client = await ConnectAsync(pipeName, workspaceId).ConfigureAwait(true);
        string uri = LspUriMapper.ToUri(declarationFile);

        LspSymbolResult loaded = await client.SymbolAsync("workspace-load", string.Empty, string.Empty, 0, 1, newName: string.Empty, Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Ready, loaded.State);

        LspSymbolResult rename = await client.SymbolAsync("rename", uri, "Widget", 0, 1, "Gadget", Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Ready, rename.State);
        Assert.False(string.IsNullOrWhiteSpace(rename.Message), "a rename must carry the server's WorkspaceEdit");

        LspRenameEditSet edits = LspRenameEdits.Parse(rename.Message);
        Assert.False(edits.HasFileRenames, "a text rename must not ask for a file move");

        // The assertion this test exists for: a single edit means resolution found the
        // declaration and nothing else, which is the failure the feature prevents.
        int editCount = edits.Documents.Sum(document => document.Edits.Count);
        Assert.True(editCount > 1, "the rename must cover every usage, not only the declaration; edits=" + editCount);

        // Both files are covered: the declaration and the file that only uses it. The
        // comparison is on the resolved path, because a server canonicalizes a URI into
        // its own form rather than echoing the one it was asked about.
        Assert.Contains(edits.Documents, document => IsSameFile(document.Uri, declarationFile));
        Assert.Contains(edits.Documents, document => IsSameFile(document.Uri, usageFile));

        // The edit set is what the rename means, and a server is free to answer with a
        // minimal diff - 'Widget' -> 'Gadget' arrives as 'Wi' -> 'Ga' over the differing
        // prefix. So the assertion is on the OUTCOME: applying every edit leaves the new
        // name everywhere and the old name nowhere.
        foreach (LspTextDocumentEdit document in edits.Documents)
        {
          string path = LspUriMapper.FromUri(document.Uri);
          string after = ApplyEdits(await File.ReadAllTextAsync(path, Ct).ConfigureAwait(true), document.Edits);
          Assert.DoesNotContain("Widget", after, StringComparison.Ordinal);
          Assert.Contains("Gadget", after, StringComparison.Ordinal);
        }

        client.Dispose();
      }
      finally
      {
        if (!daemon.HasExited)
        {
          daemon.Kill(entireProcessTree: true);
        }
      }
    }
    finally
    {
      DeleteWorkspace(root);
    }
  }
}
