using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

public class LspServerResolverTests
{
  private static LspServerResolver Fake(
    HashSet<string> existing,
    Dictionary<string, string?> pathHits,
    IReadOnlyList<string> roots,
    Action? onProbe = null)
  {
    return new LspServerResolver(
      fileExists: p => { onProbe?.Invoke(); return existing.Contains(p); },
      whereOnPath: n => pathHits.TryGetValue(n, out string? hit) ? hit : null,
      knownRoots: roots,
      serverFileName: LspServerResolver.CSharpServerFileName);
  }

  [Fact]
  public void BundledBesideApp_WinsOverPathAndKnownRoots()
  {
    string appDir = "C:\\app";
    string bundled = Path.Combine(appDir, LspServerResolver.CSharpServerFileName);
    string onPath = "C:\\tools\\" + LspServerResolver.CSharpServerFileName;
    string inRoot = "C:\\roots\\" + LspServerResolver.CSharpServerFileName;
    HashSet<string> existing = [bundled, onPath, inRoot];
    Dictionary<string, string?> pathHits = new() { [LspServerResolver.CSharpServerFileName] = onPath };
    LspServerResolver resolver = Fake(existing, pathHits, ["C:\\roots"]);
    Assert.Equal(bundled, resolver.Resolve(appDir));
  }

  [Fact]
  public void PathHit_WinsOverKnownRoots()
  {
    string onPath = "C:\\tools\\" + LspServerResolver.CSharpServerFileName;
    string inRoot = "C:\\roots\\" + LspServerResolver.CSharpServerFileName;
    HashSet<string> existing = [onPath, inRoot];
    Dictionary<string, string?> pathHits = new() { [LspServerResolver.CSharpServerFileName] = onPath };
    LspServerResolver resolver = Fake(existing, pathHits, ["C:\\roots"]);
    Assert.Equal(onPath, resolver.Resolve("C:\\app"));
  }

  [Fact]
  public void KnownRootHit_WhenNoBundledOrPath()
  {
    string inRoot = "C:\\roots\\" + LspServerResolver.CSharpServerFileName;
    HashSet<string> existing = [inRoot];
    Dictionary<string, string?> pathHits = [];
    LspServerResolver resolver = Fake(existing, pathHits, ["C:\\roots"]);
    Assert.Equal(inRoot, resolver.Resolve("C:\\app"));
  }

  [Fact]
  public void AbsentEverywhere_ReturnsNull()
  {
    LspServerResolver resolver = Fake([], [], ["C:\\roots"]);
    Assert.Null(resolver.Resolve("C:\\app"));
  }

  [Fact]
  public void Cache_RepeatedResolve_DoesNotReprobe()
  {
    int probes = 0;
    string bundled = Path.Combine("C:\\app", LspServerResolver.CSharpServerFileName);
    HashSet<string> existing = [bundled];
    LspServerResolver resolver = Fake(existing, [], [], () => probes++);
    Assert.Equal(bundled, resolver.Resolve("C:\\app"));
    int afterFirst = probes;
    Assert.Equal(bundled, resolver.Resolve("C:\\app"));
    Assert.Equal(bundled, resolver.Resolve("C:\\app"));
    Assert.Equal(afterFirst, probes);
  }

  [Fact]
  public void TrustRule_NonWellKnownServerName_Throws()
  {
    Assert.Throws<ArgumentException>(() => new LspServerResolver(
      fileExists: _ => false,
      whereOnPath: _ => null,
      knownRoots: [],
      serverFileName: "evil.exe"));
  }

  [Fact]
  public void TrustRule_WorkspaceBinary_NeverResolved()
  {
    string workspaceBinary = Path.Combine("C:\\ws", LspServerResolver.CSharpServerFileName);
    HashSet<string> existing = [workspaceBinary];
    LspServerResolver resolver = Fake(existing, [], []);
    Assert.Null(resolver.Resolve("C:\\app"));
  }

  [Fact]
  public void DownloadedCache_UsedWhenNothingElseResolves()
  {
    string downloaded = Path.Combine("C:\\cache", LspServerResolver.CSharpServerFileName);
    LspServerResolver resolver = new(
      fileExists: p => p == downloaded,
      whereOnPath: _ => null,
      knownRoots: [],
      serverFileName: LspServerResolver.CSharpServerFileName,
      downloadedServer: () => Path.Combine("C:\\cache", LspServerResolver.CSharpServerFileName));
    Assert.Equal(downloaded, resolver.Resolve("C:\\app"));
  }

  [Fact]
  public void Bundled_WinsOverDownloadedCache()
  {
    string bundled = Path.Combine("C:\\app", LspServerResolver.CSharpServerFileName);
    LspServerResolver resolver = new(
      fileExists: p => p == bundled,
      whereOnPath: _ => null,
      knownRoots: [],
      serverFileName: LspServerResolver.CSharpServerFileName,
      downloadedServer: () => Path.Combine("C:\\cache", LspServerResolver.CSharpServerFileName));
    Assert.Equal(bundled, resolver.Resolve("C:\\app"));
  }

  [Fact]
  public void DownloadedCache_IsProbedLast()
  {
    string onPath = "C:\\tools\\" + LspServerResolver.CSharpServerFileName;
    LspServerResolver resolver = new(
      fileExists: p => p == onPath,
      whereOnPath: _ => onPath,
      knownRoots: [],
      serverFileName: LspServerResolver.CSharpServerFileName,
      downloadedServer: () => Path.Combine("C:\\cache", LspServerResolver.CSharpServerFileName));
    Assert.Equal(onPath, resolver.Resolve("C:\\app"));
  }

  [Fact]
  public void VersionPin_IsRecorded() =>
    Assert.Equal(LspServerResolver.CSharpServerVersion, LspServerCatalog.CSharp.Version);

  [Fact]
  public void TryResolveForWorkspace_ProjectLess_ReturnsNull()
  {
    string root = TestTempRoot.CreateDirectory("lspprojless").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "notes.md"), "hello");
      string bundled = Path.Combine("C:\\app", LspServerResolver.CSharpServerFileName);
      HashSet<string> existing = [bundled];
      LspServerResolver resolver = Fake(existing, [], []);
      Assert.Null(resolver.TryResolveForWorkspace("C:\\app", root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void TryResolveForWorkspace_CSharpWithBundled_ReturnsLaunch()
  {
    string root = TestTempRoot.CreateDirectory("lspcsharp").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "app.sln"), "solution");
      string bundled = Path.Combine("C:\\app", LspServerResolver.CSharpServerFileName);
      HashSet<string> existing = [bundled];
      LspServerResolver resolver = Fake(existing, [], []);
      LspServerLaunch? launch = resolver.TryResolveForWorkspace("C:\\app", root);
      Assert.NotNull(launch);
      Assert.Equal("csharp", launch.LanguageId);
      Assert.Equal(bundled, launch.ServerPath);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void TryResolveForWorkspace_CSharpWithoutServer_ReturnsNull()
  {
    string root = TestTempRoot.CreateDirectory("lspnoserver").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "app.csproj"), "<Project />");
      LspServerResolver resolver = Fake([], [], []);
      Assert.Null(resolver.TryResolveForWorkspace("C:\\app", root));
    }
    finally { Directory.Delete(root, true); }
  }
}
