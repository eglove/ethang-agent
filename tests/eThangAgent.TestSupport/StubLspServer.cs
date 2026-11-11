using System.Diagnostics;
using Xunit;

namespace eThangAgent.TestSupport;

/// <summary>Builds the in-repo stub language server once per test process and places it
///     inside an install root the way a real row's server is placed, so the daemon launches
///     it through the ordinary resolver and a test can observe WHICH process answered.
///     <para>INSTALLING THE STUB is what makes a multi-language claim observable at all: a
///     starter faked inside the daemon produces no second process, so 'two servers served two
///     languages' would be an assertion about the test's own fake. Here the daemon resolves,
///     launches, and initializes a real child process per language.</para>
///     <para>The placement mirrors the production install layout: a downloaded row lands under
///     &lt;root&gt;/&lt;PackageId&gt;/&lt;Version&gt;/&lt;EntryPointRelativePath&gt;/&lt;ServerFileName&gt;, and a
///     toolchain row under &lt;root&gt;/&lt;LanguageId&gt;/&lt;Version&gt;/. The caller names the directory
///     segments, so the file lands exactly where that row's resolver looks.</para></summary>
public static class StubLspServer
{
  private static readonly Lock Gate = new();
  private static string? _outputDirectory;

  /// <summary>The stub server's built output directory, building it on first use.</summary>
  public static string Build()
  {
    lock (Gate)
    {
      if (_outputDirectory is not null && Directory.Exists(_outputDirectory))
      {
        return _outputDirectory;
      }

      string project = Path.GetFullPath(Path.Combine(
          RepoRoot(), "tests", "StubLspServer", "FakeLspServer.csproj"));
      ProcessStartInfo psi = new("dotnet", ["build", project, "-v", "q", "--nologo"])
      {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
      };
      using Process build = Process.Start(psi)!;
      string stdout = build.StandardOutput.ReadToEnd();
      string stderr = build.StandardError.ReadToEnd();
      build.WaitForExit();
      Assert.True(build.ExitCode == 0, "the stub language server failed to build: " + stdout + stderr);
      _outputDirectory = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", "net11.0");
      Assert.True(File.Exists(Path.Combine(_outputDirectory, "ethang-fake-lsp.exe")),
          "the stub language server was built but is not where its project puts it: " + _outputDirectory);
      return _outputDirectory;
    }
  }

  /// <summary>Installs the stub server inside <paramref name="installRoot"/> and answers the
  ///     entry point a resolver looks for.</summary>
  /// <param name="installRoot">The install root the daemon reads.</param>
  /// <param name="relativeDirectory">The directory segments the row's layout keys the install
  ///     by: package and version for a downloaded row, language and version for a toolchain
  ///     row.</param>
  /// <param name="entryPointRelativePath">The row's entry-point directory inside that
  ///     directory, empty when the server sits at its root.</param>
  /// <param name="serverFileName">The file name the row's resolver looks for.</param>
  public static string Install(string installRoot, string relativeDirectory,
      string entryPointRelativePath, string serverFileName)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
    ArgumentException.ThrowIfNullOrWhiteSpace(relativeDirectory);
    ArgumentException.ThrowIfNullOrWhiteSpace(serverFileName);
    ArgumentNullException.ThrowIfNull(entryPointRelativePath);
    string output = Build();
    string serverDirectory = Path.Combine(installRoot, relativeDirectory,
        entryPointRelativePath.Replace('/', Path.DirectorySeparatorChar));
    _ = Directory.CreateDirectory(serverDirectory);

    // The apphost carries its own assembly name, so the executable is copied under the file
    // name the resolver looks for while its assembly and runtime configuration keep the names
    // the apphost expects beside it.
    string target = Path.Combine(serverDirectory, serverFileName);
    File.Copy(Path.Combine(output, "ethang-fake-lsp.exe"), target, overwrite: true);
    foreach (string source in Directory.EnumerateFiles(output, "ethang-fake-lsp.*")
        .Where(file => !string.Equals(Path.GetExtension(file), ".exe", StringComparison.OrdinalIgnoreCase)))
    {
      File.Copy(source, Path.Combine(serverDirectory, Path.GetFileName(source)), overwrite: true);
    }

    Assert.True(File.Exists(target), "the stub server was installed under the wrong name: " + target);
    return target;
  }

  /// <summary>The checkout containing this assembly, found the way every other test helper
  ///     finds it.</summary>
  private static string RepoRoot()
  {
    DirectoryInfo? dir = new(typeof(StubLspServer).Assembly.Location);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    return dir?.FullName ?? throw new InvalidOperationException("the test assembly lives outside any checkout");
  }
}
