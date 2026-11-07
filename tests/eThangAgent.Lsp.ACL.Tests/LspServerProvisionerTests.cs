using System.Text;
using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

public class LspServerProvisionerTests
{
  private static byte[] PackageWith(string entryPath, string entryName, string content)
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

  private static string Hash(byte[] bytes) => Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(bytes));

  [Fact]
  public async Task AlreadyResolvable_ReturnsItWithoutDownloading()
  {
    string root = TestTempRoot.CreateDirectory("lspprov").FullName;
    try
    {
      int downloads = 0;
      LspServerProvisioner provisioner = new(
          package => { downloads++; return Task.FromResult<byte[]?>(null); },
          _ => "C:\\found\\server.exe",
          root);
      LspServerLaunch? launch = await provisioner.ResolveOrInstallAsync(LspServerCatalog.CSharp, TestContext.Current.CancellationToken);
      Assert.NotNull(launch);
      Assert.Equal("C:\\found\\server.exe", launch.ServerPath);
      Assert.Equal(0, downloads);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Absent_InstallsAndReturnsTheInstalledPath()
  {
    string root = TestTempRoot.CreateDirectory("lspprov").FullName;
    try
    {
      byte[] nupkg = PackageWith("tools/net10.0/win-x64", "server.exe", "binary");
      LspServerPackage package = new(
          LanguageId: "csharp",
          PackageId: "test.pkg",
          Version: "1.0.0",
          Sha512: Hash(nupkg),
          EntryPointRelativePath: "tools/net10.0/win-x64",
          ServerFileName: "server.exe");
      LspServerProvisioner provisioner = new(
          _ => Task.FromResult<byte[]?>(nupkg),
          _ => null,
          root);
      LspServerLaunch? launch = await provisioner.ResolveOrInstallAsync(package, TestContext.Current.CancellationToken);
      Assert.NotNull(launch);
      Assert.True(File.Exists(launch.ServerPath));
      Assert.Equal(package.Version, launch.Version);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Absent_AndDownloadFails_ReturnsNull()
  {
    string root = TestTempRoot.CreateDirectory("lspprov").FullName;
    try
    {
      LspServerProvisioner provisioner = new(
          _ => Task.FromResult<byte[]?>(null),
          _ => null,
          root);
      Assert.Null(await provisioner.ResolveOrInstallAsync(LspServerCatalog.CSharp, TestContext.Current.CancellationToken));
    }
    finally { Directory.Delete(root, true); }
  }
}
