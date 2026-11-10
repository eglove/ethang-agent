using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

public class LspInstallStrategyTests
{
  private static string TempRoot() => TestTempRoot.CreateDirectory("lspstrategy").FullName;

  private static LspLanguageRow ArchiveRow(
      string packageId,
      string entryPath,
      string serverFileName,
      string checksum,
      string languageId = "rust",
      string version = "1.0.0") => new(
      LanguageId: languageId,
      DisplayName: languageId,
      InstallKind: LspInstallKind.Archive,
      PrerequisiteExecutable: null,
      Extensions: [".rs"],
      ManifestNames: [],
      PackageId: packageId,
      Version: version,
      Checksum: checksum,
      EntryPointRelativePath: entryPath,
      ServerFileName: serverFileName,
      Arguments: [],
      ArchiveAddress: "https://example.invalid/" + packageId + ".zip");

  private static LspLanguageRow ToolchainRow(
      string languageId,
      string packageId,
      string serverFileName,
      string prerequisite = "node",
      string version = "1.0.0") => new(
      LanguageId: languageId,
      DisplayName: languageId,
      InstallKind: LspInstallKind.Toolchain,
      PrerequisiteExecutable: prerequisite,
      Extensions: [".ts"],
      ManifestNames: [],
      PackageId: packageId,
      Version: version,
      Checksum: null,
      EntryPointRelativePath: string.Empty,
      ServerFileName: serverFileName,
      Arguments: []);

  private static LspLanguageRow NuGetRow(string packageId, string checksum, string version = "1.0.0") => new(
      LanguageId: "csharp",
      DisplayName: "C#",
      InstallKind: LspInstallKind.NuGet,
      PrerequisiteExecutable: null,
      Extensions: [".cs"],
      ManifestNames: [],
      PackageId: packageId,
      Version: version,
      Checksum: checksum,
      EntryPointRelativePath: "tools/net10.0/win-x64",
      ServerFileName: "server.exe",
      Arguments: []);

  private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

  private static string Sha512(byte[] bytes) => Convert.ToBase64String(SHA512.HashData(bytes));

  private static byte[] Zip(params (string Name, string Content)[] entries)
  {
    using MemoryStream stream = new();
    using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
    {
      foreach ((string name, string content) in entries)
      {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using Stream body = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        body.Write(bytes, 0, bytes.Length);
      }
    }

    return stream.ToArray();
  }

  private static byte[] TarGz(params (string Name, string Content)[] entries)
  {
    using MemoryStream tar = new();
    foreach ((string name, string content) in entries)
    {
      byte[] body = Encoding.UTF8.GetBytes(content);
      byte[] header = new byte[512];
      byte[] nameBytes = Encoding.UTF8.GetBytes(name);
      Array.Copy(nameBytes, header, nameBytes.Length);
      for (int index = 148; index < 156; index++)
      {
        header[index] = (byte)' ';
      }

      byte[] size = Encoding.ASCII.GetBytes(Convert.ToString(body.Length, 8).PadLeft(11, '0') + "\0");
      Array.Copy(size, 0, header, 124, 12);
      header[156] = (byte)'0';
      tar.Write(header);
      tar.Write(body);
      tar.Write(new byte[(512 - (body.Length % 512)) % 512]);
    }

    tar.Write(new byte[1024]);
    tar.Position = 0;
    using MemoryStream gzip = new();
    using (GZipStream compressor = new(gzip, CompressionLevel.SmallestSize, leaveOpen: true))
    {
      tar.CopyTo(compressor);
    }

    return gzip.ToArray();
  }

  [Fact]
  public async Task Archive_ChecksumMatches_InstallsUnderPackageAndVersion()
  {
    string root = TempRoot();
    try
    {
      byte[] artifact = Zip(("rust-analyzer.exe", "binary"));
      LspLanguageRow row = ArchiveRow("rust-analyzer-x86_64-pc-windows-msvc", string.Empty, "rust-analyzer.exe", Sha256(artifact));
      int downloads = 0;
      LspArchiveInstallStrategy strategy = new(_ => { downloads++; return Task.FromResult<byte[]?>(artifact); });
      LspInstallResult result = await strategy.InstallAsync(row, root, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.Installed, result.Outcome);
      Assert.Equal(1, downloads);
      string expected = Path.Combine(root, row.PackageId, row.Version, row.ServerFileName);
      Assert.True(File.Exists(expected), "the entry point must exist under <root>/<package>/<version>");
      Assert.Equal(expected, strategy.ResolveInstalled(row, root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Archive_OneByteAltered_ReportsHashMismatchAndLeavesNoPackageDirectory()
  {
    string root = TempRoot();
    try
    {
      byte[] artifact = Zip(("rust-analyzer.exe", "binary"));
      LspLanguageRow row = ArchiveRow("rust-analyzer-x86_64-pc-windows-msvc", string.Empty, "rust-analyzer.exe", Sha256(artifact));
      byte[] altered = [.. artifact];
      altered[^1] ^= 0xFF;
      LspArchiveInstallStrategy strategy = new(_ => Task.FromResult<byte[]?>(altered));
      LspInstallResult result = await strategy.InstallAsync(row, root, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.HashMismatch, result.Outcome);
      Assert.False(Directory.Exists(Path.Combine(root, row.PackageId)),
          "a mismatched archive must extract nothing");
      Assert.Null(strategy.ResolveInstalled(row, root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Archive_TarGz_ExtractsTheEntryPointAndItsNeighbours()
  {
    string root = TempRoot();
    try
    {
      byte[] artifact = TarGz(("bin/lua-language-server.exe", "binary"), ("bin/main.lua", "script"));
      LspLanguageRow row = ArchiveRow("lua-language-server-3.19.2-win32-x64", "bin", "lua-language-server.exe", Sha256(artifact), languageId: "lua");
      LspArchiveInstallStrategy strategy = new(_ => Task.FromResult<byte[]?>(artifact));
      LspInstallResult result = await strategy.InstallAsync(row, root, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.Installed, result.Outcome);
      string? resolved = strategy.ResolveInstalled(row, root);
      Assert.NotNull(resolved);
      Assert.Equal(
          Path.Combine(root, row.PackageId, row.Version, "bin", "lua-language-server.exe"),
          resolved);
      Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(resolved)!, "main.lua")),
          "a server that ships dependencies must be installed with them");
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Archive_NoEntryPointInTheArtifact_ReportsDownloadFailed()
  {
    string root = TempRoot();
    try
    {
      byte[] artifact = Zip(("docs/readme.md", "text"));
      LspLanguageRow row = ArchiveRow("rust-analyzer-x86_64-pc-windows-msvc", string.Empty, "rust-analyzer.exe", Sha256(artifact));
      LspArchiveInstallStrategy strategy = new(_ => Task.FromResult<byte[]?>(artifact));
      LspInstallResult result = await strategy.InstallAsync(row, root, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.DownloadFailed, result.Outcome);
      Assert.Null(strategy.ResolveInstalled(row, root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task NuGet_ChecksumMatches_InstallsExactlyAsTheCSharpServerDoes()
  {
    string root = TempRoot();
    try
    {
      byte[] artifact = Zip(("tools/net10.0/win-x64/server.exe", "binary"), ("tools/net10.0/win-x64/server.dll", "dependency"));
      LspLanguageRow row = NuGetRow("roslyn-language-server.win-x64", Sha512(artifact));
      LspNuGetInstallStrategy strategy = new(_ => Task.FromResult<byte[]?>(artifact));
      LspInstallResult result = await strategy.InstallAsync(row, root, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.Installed, result.Outcome);
      string? resolved = strategy.ResolveInstalled(row, root);
      Assert.NotNull(resolved);
      Assert.Equal(
          Path.Combine(root, row.PackageId, row.Version, "tools", "net10.0", "win-x64", "server.exe"),
          resolved);
      Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(resolved)!, "server.dll")));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task NuGet_ChecksumMismatch_ExtractsNothing()
  {
    string root = TempRoot();
    try
    {
      byte[] artifact = Zip(("tools/net10.0/win-x64/server.exe", "binary"));
      LspLanguageRow row = NuGetRow("roslyn-language-server.win-x64", Sha512([1, 2, 3]));
      LspNuGetInstallStrategy strategy = new(_ => Task.FromResult<byte[]?>(artifact));
      LspInstallResult result = await strategy.InstallAsync(row, root, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.HashMismatch, result.Outcome);
      Assert.False(Directory.Exists(Path.Combine(root, row.PackageId)));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Toolchain_PrerequisitePresent_InstallsIntoTheAppOwnedPrefix()
  {
    string root = TempRoot();
    try
    {
      LspLanguageRow row = ToolchainRow("typescript", "typescript-language-server", "typescript-language-server.cmd", version: "6.0.3");
      string prefix = Path.Combine(root, "typescript", "6.0.3");
      List<LspToolchainCommand> commands = [];
      LspToolchainInstallStrategy strategy = new(
          new LspPrerequisiteProbe(_ => PrerequisiteProbeResult.Found("C:\\nodejs\\node.exe")),
          run: (command, _) =>
          {
            commands.Add(command);
            Directory.CreateDirectory(prefix);
            File.WriteAllText(Path.Combine(prefix, row.ServerFileName), "shim");
            return Task.FromResult(0);
          });
      LspInstallResult result = await strategy.InstallAsync(row, root, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.Installed, result.Outcome);
      LspToolchainCommand command = Assert.Single(commands);
      Assert.Equal(Path.Combine("C:\\nodejs", "npm.cmd"), command.Executable);
      Assert.Contains(prefix, command.Arguments);
      Assert.Contains("typescript-language-server@6.0.3", command.Arguments);
      Assert.Equal(Path.Combine(prefix, row.ServerFileName), strategy.ResolveInstalled(row, root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Toolchain_PrefixHoldingASpace_ReachesTheCommandAsOneArgument()
  {
    string root = TempRoot();
    try
    {
      string spaced = Path.Combine(root, "with space");
      LspLanguageRow row = ToolchainRow("typescript", "typescript-language-server", "typescript-language-server.cmd");
      LspToolchainCommand? observed = null;
      LspToolchainInstallStrategy strategy = new(
          new LspPrerequisiteProbe(_ => PrerequisiteProbeResult.Found("C:\\nodejs\\node.exe")),
          run: (command, _) => { observed = command; return Task.FromResult(1); });
      _ = await strategy.InstallAsync(row, spaced, TestContext.Current.CancellationToken);
      Assert.NotNull(observed);
      Assert.Contains(Path.Combine(spaced, "typescript", "1.0.0"), observed.Arguments);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Toolchain_PrerequisiteAbsent_ReportsPrerequisiteMissingAndRunsNothing()
  {
    string root = TempRoot();
    try
    {
      LspLanguageRow row = ToolchainRow("python", "pyright", "pyright-langserver.cmd");
      int runs = 0;
      LspToolchainInstallStrategy strategy = new(
          new LspPrerequisiteProbe(_ => PrerequisiteProbeResult.Missing),
          run: (_, _) => { runs++; return Task.FromResult(0); });
      LspInstallResult result = await strategy.InstallAsync(row, root, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.PrerequisiteMissing, result.Outcome);
      Assert.Equal(0, runs);
      Assert.False(Directory.Exists(Path.Combine(root, "python")), "nothing is written when the toolchain is absent");
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Toolchain_GoWithoutTheGoToolchain_ReportsPrerequisiteMissing()
  {
    string root = TempRoot();
    try
    {
      LspLanguageRow row = ToolchainRow("go", "golang.org/x/tools/gopls", "gopls.exe", prerequisite: "go");
      int runs = 0;
      LspToolchainInstallStrategy strategy = new(
          new LspPrerequisiteProbe(_ => PrerequisiteProbeResult.Missing),
          run: (_, _) => { runs++; return Task.FromResult(0); });
      Assert.Equal(LspInstallOutcome.PrerequisiteMissing,
          (await strategy.InstallAsync(row, root, TestContext.Current.CancellationToken)).Outcome);
      Assert.Equal(0, runs);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Toolchain_CommandExitsZeroWithoutAnEntryPoint_ReportsDownloadFailed()
  {
    string root = TempRoot();
    try
    {
      LspLanguageRow row = ToolchainRow("typescript", "typescript-language-server", "typescript-language-server.cmd");
      LspToolchainInstallStrategy strategy = new(
          new LspPrerequisiteProbe(_ => PrerequisiteProbeResult.Found("C:\\nodejs\\node.exe")),
          run: (_, _) => Task.FromResult(0));
      LspInstallResult result = await strategy.InstallAsync(row, root, TestContext.Current.CancellationToken);
      Assert.Equal(LspInstallOutcome.DownloadFailed, result.Outcome);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Toolchain_CommandFails_ReportsDownloadFailed()
  {
    string root = TempRoot();
    try
    {
      LspLanguageRow row = ToolchainRow("typescript", "typescript-language-server", "typescript-language-server.cmd");
      LspToolchainInstallStrategy strategy = new(
          new LspPrerequisiteProbe(_ => PrerequisiteProbeResult.Found("C:\\nodejs\\node.exe")),
          run: (_, _) => Task.FromResult(1));
      Assert.Equal(LspInstallOutcome.DownloadFailed,
          (await strategy.InstallAsync(row, root, TestContext.Current.CancellationToken)).Outcome);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task SecondInstall_OfAnInstalledRow_DownloadsNothingAndSpawnsNothing()
  {
    string root = TempRoot();
    try
    {
      byte[] artifact = Zip(("rust-analyzer.exe", "binary"));
      LspLanguageRow archive = ArchiveRow("rust-analyzer-x86_64-pc-windows-msvc", string.Empty, "rust-analyzer.exe", Sha256(artifact));
      int downloads = 0;
      int runs = 0;
      LspServerInstaller installer = new(root, new Dictionary<LspInstallKind, ILspInstallStrategy>
      {
        [LspInstallKind.Archive] = new LspArchiveInstallStrategy(_ => { downloads++; return Task.FromResult<byte[]?>(artifact); }),
        [LspInstallKind.Toolchain] = new LspToolchainInstallStrategy(
            new LspPrerequisiteProbe(_ => PrerequisiteProbeResult.Found("C:\\nodejs\\node.exe")),
            run: (_, _) => { runs++; return Task.FromResult(0); }),
      });
      LspLanguageRow toolchain = ToolchainRow("typescript", "typescript-language-server", "typescript-language-server.cmd");
      string prefix = Path.Combine(root, "typescript", toolchain.Version);
      Directory.CreateDirectory(prefix);
      File.WriteAllText(Path.Combine(prefix, toolchain.ServerFileName), "shim");
      Assert.Equal(LspInstallOutcome.Installed,
          (await installer.InstallAsync(archive, TestContext.Current.CancellationToken)).Outcome);
      Assert.Equal(LspInstallOutcome.Installed,
          (await installer.InstallAsync(archive, TestContext.Current.CancellationToken)).Outcome);
      Assert.Equal(1, downloads);
      Assert.Equal(LspInstallOutcome.Installed,
          (await installer.InstallAsync(toolchain, TestContext.Current.CancellationToken)).Outcome);
      Assert.Equal(LspInstallOutcome.Installed,
          (await installer.InstallAsync(toolchain, TestContext.Current.CancellationToken)).Outcome);
      Assert.Equal(0, runs);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task FailedInstall_IsRememberedPerRow_TwoRowsSharingAPackageStayDistinct()
  {
    string root = TempRoot();
    try
    {
      int downloads = 0;
      LspServerInstaller installer = new(root, new Dictionary<LspInstallKind, ILspInstallStrategy>
      {
        [LspInstallKind.Archive] = new LspArchiveInstallStrategy(_ => { downloads++; return Task.FromResult<byte[]?>(null); }),
      });
      LspLanguageRow first = ArchiveRow("shared.package", string.Empty, "a.exe", Sha256([1]), languageId: "rust");
      LspLanguageRow second = ArchiveRow("shared.package", string.Empty, "b.exe", Sha256([1]), languageId: "lua");
      Assert.Equal(LspInstallOutcome.DownloadFailed,
          (await installer.InstallAsync(first, TestContext.Current.CancellationToken)).Outcome);
      Assert.Equal(LspInstallOutcome.DownloadFailed,
          (await installer.InstallAsync(first, TestContext.Current.CancellationToken)).Outcome);
      Assert.Equal(1, downloads);
      Assert.Equal(LspInstallOutcome.DownloadFailed,
          (await installer.InstallAsync(second, TestContext.Current.CancellationToken)).Outcome);
      Assert.Equal(2, downloads);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void ResolveInstalled_ReturnsNullForEveryKind_WhenNothingIsOnDisk()
  {
    string root = TempRoot();
    try
    {
      LspServerInstaller installer = new(root, LspServerInstaller.DefaultStrategies());
      foreach (LspLanguageRow row in LspServerCatalog.All)
      {
        Assert.Null(installer.ResolveInstalled(row));
      }
    }
    finally { Directory.Delete(root, true); }
  }
}
