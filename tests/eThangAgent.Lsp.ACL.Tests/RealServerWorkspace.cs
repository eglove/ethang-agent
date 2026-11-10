using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The fixtures and the two helpers the real-server tier shares: the smallest
///     workspace a language's server will analyse, and the install of that server into a temp
///     root from its pinned artifact.
///     <para>NOTHING HERE IS A TEST, so this file carries no trait and spawns nothing.</para>
///     </summary>
internal static class RealServerWorkspace
{
  /// <summary>The smallest workspace a language's server will analyse: a manifest plus one
  ///     source file that declares the queried symbol before it uses it, so the first
  ///     occurrence of the name is the declaration. Returns the source file's path and its
  ///     workspace-relative path.</summary>
  public static (string File, string Relative) Write(string root, string languageId)
  {
    if (string.Equals(languageId, "csharp", StringComparison.Ordinal))
    {
      File.WriteAllText(Path.Combine(root, "probe.csproj"),
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(root, "probe.slnx"),
          "<Solution>\n  <Project Path=\"probe.csproj\" />\n</Solution>\n");
      File.WriteAllLines(Path.Combine(root, "Program.cs"),
      [
          "namespace Probe;",
          "",
          "public static class Program",
          "{",
          "  public static void Greet()",
          "  {",
          "  }",
          "",
          "  public static void Main()",
          "  {",
          "    Greet();",
          "  }",
          "}",
      ]);
      return (Path.Combine(root, "Program.cs"), "Program.cs");
    }

    _ = Directory.CreateDirectory(Path.Combine(root, "src"));
    File.WriteAllLines(Path.Combine(root, "Cargo.toml"),
    [
        "[package]",
        "name = \"probe\"",
        "version = \"0.1.0\"",
        "edition = \"2021\"",
        "",
    ]);
    File.WriteAllLines(Path.Combine(root, "src", "main.rs"),
    [
        "fn greet() {",
        "}",
        "",
        "fn main() {",
        "    greet();",
        "}",
    ]);
    return (Path.Combine(root, "src", "main.rs"), "src/main.rs");
  }

  /// <summary>Installs a row's server from its pinned artifact into a temp root and returns
  ///     the entry point. The app cache is deliberately not used: an install that finds a
  ///     server already in place downloads and verifies nothing.</summary>
  public static Task<string> InstallAsync(LspLanguageRow row, string label, CancellationToken ct)
    => InstallIntoAsync(row, TestTempRoot.CreateDirectory("real-install-" + label).FullName, ct);

  /// <summary>The same install into a root the caller names, so a test can assert where the
  ///     entry point landed.</summary>
  public static async Task<string> InstallIntoAsync(LspLanguageRow row, string installRoot, CancellationToken ct)
  {
    LspServerInstaller installer = new(installRoot, LspServerInstaller.DefaultStrategies());
    Assert.Null(installer.ResolveInstalled(row));
    Assert.Equal(LspInstallOutcome.Installed, (await installer.InstallAsync(row, ct).ConfigureAwait(true)).Outcome);
    string? serverPath = installer.ResolveInstalled(row);
    Assert.NotNull(serverPath);
    return serverPath;
  }

  /// <summary>The line a fixture declares <paramref name="symbol"/> on: the first line that
  ///     names it, which is the declaration in a fixture that declares before it calls.</summary>
  public static int DeclarationLine(string file, string symbol)
  {
    string[] lines = File.ReadAllLines(file);
    for (int index = 0; index < lines.Length; index++)
    {
      if (lines[index].Contains(symbol, StringComparison.Ordinal))
      {
        return index;
      }
    }

    throw new InvalidOperationException("the fixture never names " + symbol);
  }
}
