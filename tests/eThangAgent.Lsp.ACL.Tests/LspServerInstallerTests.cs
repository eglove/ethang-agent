using System.Security.Cryptography;
using System.Text;
using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

public class LspServerInstallerTests
{
  private static string TempRoot() => TestTempRoot.CreateDirectory("lspinstall").FullName;

  private static LspLanguageRow Row(string checksum, string version = "1.0.0") => new(
      LanguageId: "csharp",
      DisplayName: "C#",
      InstallKind: LspInstallKind.NuGet,
      PrerequisiteExecutable: null,
      Extensions: [".cs"],
      ManifestNames: [],
      PackageId: "test.pkg",
      Version: version,
      Checksum: checksum,
      EntryPointRelativePath: "tools/net10.0/win-x64",
      ServerFileName: "server.exe",
      Arguments: []);

  private static LspServerInstaller Installer(string root, Func<LspLanguageRow, Task<byte[]?>> download) =>
      new(root, new Dictionary<LspInstallKind, ILspInstallStrategy>
      {
        [LspInstallKind.NuGet] = new LspNuGetInstallStrategy(download),
      });

  private static byte[] BuildPackage(string entryPath, string entryName, string content)
  {
    using MemoryStream ms = new();
    using (System.IO.Compression.ZipArchive zip = new(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
      System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry(entryPath + "/" + entryName);
      using Stream s = entry.Open();
      byte[] bytes = Encoding.UTF8.GetBytes(content);
      s.Write(bytes, 0, bytes.Length);
    }
    return ms.ToArray();
  }

  private static byte[] BuildPackageWithSibling(
      string entryPath, string entryName, string content, string siblingName, string siblingContent)
  {
    using MemoryStream ms = new();
    using (System.IO.Compression.ZipArchive zip = new(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
      foreach ((string name, string body) in new[] { (entryName, content), (siblingName, siblingContent) })
      {
        System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry(entryPath + "/" + name);
        using Stream s = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        s.Write(bytes, 0, bytes.Length);
      }
    }
    return ms.ToArray();
  }

  private static string Hash(byte[] bytes) => Convert.ToBase64String(SHA512.HashData(bytes));

  [Fact]
  public async Task Install_HashMismatch_RejectsAndLeavesNothingResolvable()
  {
    string root = TempRoot();
    try
    {
      byte[] nupkg = BuildPackage("tools/net10.0/win-x64", "server.exe", "binary");
      LspLanguageRow row = Row(Hash([1, 2, 3]));
      LspServerInstaller installer = Installer(root, _ => Task.FromResult<byte[]?>(nupkg));
      LspInstallResult result = await installer.InstallAsync(row, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.HashMismatch, result.Outcome);
      Assert.Null(installer.ResolveInstalled(row));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Install_VerifiedPackage_ExtractsOnlyEntryPointAndResolves()
  {
    string root = TempRoot();
    try
    {
      byte[] nupkg = BuildPackage("tools/net10.0/win-x64", "server.exe", "binary");
      LspLanguageRow row = Row(Hash(nupkg));
      LspServerInstaller installer = Installer(root, _ => Task.FromResult<byte[]?>(nupkg));
      LspInstallResult result = await installer.InstallAsync(row, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.Installed, result.Outcome);
      string? resolved = installer.ResolveInstalled(row);
      Assert.NotNull(resolved);
      Assert.True(File.Exists(resolved));
      Assert.Equal("binary", File.ReadAllText(resolved));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Install_VerifiedPackage_BringsTheWholeEntryPointDirectory()
  {
    string root = TempRoot();
    try
    {
      byte[] nupkg = BuildPackageWithSibling("tools/net10.0/win-x64", "server.exe", "binary", "server.dll", "dependency");
      LspLanguageRow row = Row(Hash(nupkg));
      LspServerInstaller installer = Installer(root, _ => Task.FromResult<byte[]?>(nupkg));
      LspInstallResult result = await installer.InstallAsync(row, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.Installed, result.Outcome);
      string? resolved = installer.ResolveInstalled(row);
      Assert.NotNull(resolved);
      string sibling = Path.Combine(Path.GetDirectoryName(resolved)!, "server.dll");
      Assert.True(File.Exists(sibling), "a server that ships dependencies must be installed with them");
      Assert.Equal("dependency", File.ReadAllText(sibling));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Install_DownloadFails_ReportsUnavailableAndCachesTheFailure()
  {
    string root = TempRoot();
    try
    {
      int attempts = 0;
      LspLanguageRow row = Row("irrelevant");
      LspServerInstaller installer = Installer(root, _ => { attempts++; return Task.FromResult<byte[]?>(null); });
      LspInstallResult first = await installer.InstallAsync(row, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.DownloadFailed, first.Outcome);
      LspInstallResult second = await installer.InstallAsync(row, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.DownloadFailed, second.Outcome);
      Assert.Equal(1, attempts);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Install_ConcurrentCallers_ExtractOnceAndBothSucceed()
  {
    string root = TempRoot();
    try
    {
      byte[] nupkg = BuildPackage("tools/net10.0/win-x64", "server.exe", "binary");
      LspLanguageRow row = Row(Hash(nupkg));
      int downloads = 0;
      LspServerInstaller installer = Installer(root, _ => { Interlocked.Increment(ref downloads); return Task.FromResult<byte[]?>(nupkg); });
      Task<LspInstallResult> a = installer.InstallAsync(row, TestContext.Current.CancellationToken);
      Task<LspInstallResult> b = installer.InstallAsync(row, TestContext.Current.CancellationToken);
      LspInstallResult[] results = await Task.WhenAll(a, b);
      Assert.All(results, r => Assert.Equal(LspInstallOutcome.Installed, r.Outcome));
      Assert.Equal(1, downloads);
      Assert.NotNull(installer.ResolveInstalled(row));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void ResolveInstalled_NothingInstalled_ReturnsNull()
  {
    string root = TempRoot();
    try
    {
      LspServerInstaller installer = Installer(root, _ => Task.FromResult<byte[]?>(null));
      Assert.Null(installer.ResolveInstalled(Row("x")));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void Catalog_CSharpRow_IsPinnedToARoslynServerWithAHash()
  {
    LspLanguageRow? row = LspServerCatalog.ForLanguage("csharp");
    Assert.NotNull(row);
    Assert.Contains("roslyn-language-server", row.PackageId, StringComparison.Ordinal);
    Assert.Equal(88, row.Checksum!.Length);
    Assert.EndsWith("roslyn-language-server.exe", row.ServerFileName, StringComparison.Ordinal);
  }

  [Fact]
  public void Catalog_UnknownLanguage_ReturnsNull() =>
      Assert.Null(LspServerCatalog.ForLanguage("cobol"));
}
