using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>Drives the REAL bundled Roslyn server through the production daemon
/// executable and the production pipe protocol to prove symbol navigation resolves
/// members that a server reports with their signature attached. Opt-in by trait - it
/// needs a built daemon and a machine where Roslyn can load the workspace - and out of
/// the default suite. Run it with --filter-trait "Category=RealLspServer".</summary>
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
      "}\n";

  [Fact]
  [Trait("Category", "RealLspServer")]
  public async Task MemberSymbols_ResolveAgainstTheRealServer()
  {
    if (Environment.GetEnvironmentVariable("ETHANG_LSP_REAL_SERVER") is not "1")
    {
      Assert.Skip("Opt-in: set ETHANG_LSP_REAL_SERVER=1 with a built daemon and a resolvable Roslyn server.");
    }

    string daemonExe = LspDaemonExeLocator.CreateDefault().Resolve();
    Assert.True(File.Exists(daemonExe), "the language-server daemon must be built first: " + daemonExe);

    string root = Directory.CreateTempSubdirectory("ethang-lsp-symbol").FullName;
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

        LspSymbolResult document = await client.SymbolAsync("document-symbols", uri, string.Empty, 0, 1, Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Ready, document.State);
        Assert.NotEmpty(document.Sites);

        // Every document symbol names its own file: a site rendered without a path is
        // unactionable to the caller.
        Assert.All(document.Sites, site => Assert.Equal(uri, site.Uri));

        // A member the server reports as 'Bump(int) : int' must still resolve by its
        // declared name - the defect this test exists to prevent.
        LspSymbolResult definition = await client.SymbolAsync("definition", uri, "Bump", 0, 1, Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Ready, definition.State);
        Assert.NotEmpty(definition.Sites);

        LspSymbolResult hover = await client.SymbolAsync("hover", uri, "Bump", 0, 1, Ct).ConfigureAwait(true);
        Assert.Equal(LspSymbolState.Ready, hover.State);
        Assert.False(string.IsNullOrWhiteSpace(hover.Message), "hover must carry the server's contents, not a bare position");

        LspSymbolResult field = await client.SymbolAsync("definition", uri, "_count", 0, 1, Ct).ConfigureAwait(true);
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
}
