namespace eThangAgent.Lsp.ACL;

/// <summary>The servers this app can install, one row per language. Adding a language is a
///     row here, not a new code path: install, detection and routing read the row and know
///     nothing about any particular server.</summary>
public static class LspServerCatalog
{
  private static readonly LspLanguageRow CSharpRow = new(
      LanguageId: "csharp",
      DisplayName: "C#",
      InstallKind: LspInstallKind.NuGet,
      PrerequisiteExecutable: null,
      Extensions: [".cs"],
      ManifestNames: [".sln", ".slnx", ".csproj"],
      PackageId: "roslyn-language-server.win-x64",
      Version: "5.13.0-1.26514.10",
      Checksum: "+bE9gCtusnlR0SVlFdfc0remupBvkNf8y2NKNp7CrCDEfbZMgCfucEMyQ/c56BxxmVy8nKDxnYpL5KnziLWo1Q==",
      EntryPointRelativePath: "tools/net10.0/win-x64",
      ServerFileName: "roslyn-language-server.exe",
      Arguments: []);

  private static readonly LspLanguageRow TypeScriptRow = new(
      LanguageId: "typescript",
      DisplayName: "TypeScript / JavaScript",
      InstallKind: LspInstallKind.Toolchain,
      PrerequisiteExecutable: "node",
      Extensions: [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"],
      ManifestNames: ["package.json", "tsconfig.json"],
      PackageId: "typescript-language-server",
      Version: "6.0.3",
      Checksum: null,
      EntryPointRelativePath: "",
      ServerFileName: "typescript-language-server.cmd",
      Arguments: ["--stdio"]);

  private static readonly LspLanguageRow PythonRow = new(
      LanguageId: "python",
      DisplayName: "Python",
      InstallKind: LspInstallKind.Toolchain,
      PrerequisiteExecutable: "node",
      Extensions: [".py", ".pyi"],
      ManifestNames: ["pyproject.toml", "setup.py", "requirements.txt", "Pipfile"],
      PackageId: "pyright",
      Version: "1.1.414",
      Checksum: null,
      EntryPointRelativePath: "",
      ServerFileName: "pyright-langserver.cmd",
      Arguments: ["--stdio"]);

  private static readonly LspLanguageRow RustRow = new(
      LanguageId: "rust",
      DisplayName: "Rust",
      InstallKind: LspInstallKind.Archive,
      PrerequisiteExecutable: null,
      Extensions: [".rs"],
      ManifestNames: ["Cargo.toml"],
      PackageId: "rust-analyzer-x86_64-pc-windows-msvc",
      Version: "2026-11-09",
      Checksum: "973ab11e84955a7f8e3cf01ccd98a33f3ecbd6d4a14bf59bb164aa9e831c6fef",
      EntryPointRelativePath: "",
      ServerFileName: "rust-analyzer.exe",
      Arguments: [],
      ArchiveAddress: "https://github.com/rust-lang/rust-analyzer/releases/download/2026-11-09/rust-analyzer-x86_64-pc-windows-msvc.zip");

  private static readonly LspLanguageRow GoRow = new(
      LanguageId: "go",
      DisplayName: "Go",
      InstallKind: LspInstallKind.Toolchain,
      PrerequisiteExecutable: "go",
      Extensions: [".go"],
      ManifestNames: ["go.mod"],
      PackageId: "golang.org/x/tools/gopls",
      Version: "v0.52.0",
      Checksum: null,
      EntryPointRelativePath: "",
      ServerFileName: "gopls.exe",
      Arguments: []);

  private static readonly LspLanguageRow JavaRow = new(
      LanguageId: "java",
      DisplayName: "Java",
      InstallKind: LspInstallKind.Archive,
      PrerequisiteExecutable: "java",
      Extensions: [".java"],
      ManifestNames: ["pom.xml", "build.gradle", "build.gradle.kts"],
      PackageId: "jdt-language-server-1.63.0-202610191322",
      Version: "1.63.0",
      Checksum: "b1f2abe0b3cccb462cf64fb4a5587c7d09df60cf8be7748b152cbd89d018bdc4",
      EntryPointRelativePath: "",
      ServerFileName: "java.exe",
      Arguments:
      [
          "-Declipse.application=org.eclipse.jdt.ls.core.id1",
          "-Dosgi.bundles.defaultStartLevel=4",
          "-Declipse.product=org.eclipse.jdt.ls.core.product",
          "-jar",
          "plugins/org.eclipse.equinox.launcher_1.8.0.v20260804-1928.jar",
          "-configuration",
          "config_win",
          "-data",
          ".",
      ],
      ArchiveAddress: "https://download.eclipse.org/jdtls/milestones/1.63.0/jdt-language-server-1.63.0-202610191322.tar.gz");

  private static readonly LspLanguageRow LuaRow = new(
      LanguageId: "lua",
      DisplayName: "Lua",
      InstallKind: LspInstallKind.Archive,
      PrerequisiteExecutable: null,
      Extensions: [".lua"],
      ManifestNames: [".luarc.json"],
      PackageId: "lua-language-server-3.19.2-win32-x64",
      Version: "3.19.2",
      Checksum: "4a3dc851b3b31e7b75a23f645d1ba4a2137579a55f5fa4edbb17a3e2a6254044",
      EntryPointRelativePath: "bin",
      ServerFileName: "lua-language-server.exe",
      Arguments: [],
      ArchiveAddress: "https://github.com/LuaLS/lua-language-server/releases/download/3.19.2/lua-language-server-3.19.2-win32-x64.zip");

  private static readonly LspLanguageRow PhpRow = new(
      LanguageId: "php",
      DisplayName: "PHP",
      InstallKind: LspInstallKind.Toolchain,
      PrerequisiteExecutable: "node",
      Extensions: [".php"],
      ManifestNames: ["composer.json"],
      PackageId: "intelephense",
      Version: "1.18.5",
      Checksum: null,
      EntryPointRelativePath: "",
      ServerFileName: "intelephense.cmd",
      Arguments: ["--stdio"]);

  private static readonly LspLanguageRow RubyRow = new(
      LanguageId: "ruby",
      DisplayName: "Ruby",
      InstallKind: LspInstallKind.Toolchain,
      PrerequisiteExecutable: "gem",
      Extensions: [".rb", ".rake"],
      ManifestNames: ["Gemfile"],
      PackageId: "solargraph",
      Version: "0.62.1",
      Checksum: null,
      EntryPointRelativePath: "",
      ServerFileName: "solargraph.cmd",
      Arguments: ["stdio"]);

  private static readonly LspLanguageRow BashRow = new(
      LanguageId: "bash",
      DisplayName: "Bash",
      InstallKind: LspInstallKind.Toolchain,
      PrerequisiteExecutable: "node",
      Extensions: [".sh", ".bash"],
      ManifestNames: [],
      PackageId: "bash-language-server",
      Version: "5.8.1",
      Checksum: null,
      EntryPointRelativePath: "",
      ServerFileName: "bash-language-server.cmd",
      Arguments: ["start"]);

  /// <summary>Every language this app can serve, in the order the settings page lists them.</summary>
  public static IReadOnlyList<LspLanguageRow> All { get; } =
  [
      CSharpRow,
      TypeScriptRow,
      PythonRow,
      RustRow,
      GoRow,
      JavaRow,
      LuaRow,
      PhpRow,
      RubyRow,
      BashRow,
  ];

  /// <summary>The C# row, the one language this app installs without being asked.</summary>
  public static LspLanguageRow CSharp => CSharpRow;

  /// <summary>The row serving a language, or null when the language has none. A language with
  ///     no row is the ordinary absent-server case, never an error.</summary>
  public static LspLanguageRow? ForLanguage(string? languageId)
  {
    if (string.IsNullOrWhiteSpace(languageId))
    {
      return null;
    }

    foreach (LspLanguageRow row in All)
    {
      if (string.Equals(row.LanguageId, languageId, StringComparison.OrdinalIgnoreCase))
      {
        return row;
      }
    }

    return null;
  }
}
