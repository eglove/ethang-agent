namespace eThangAgent.Lsp.ACL.Tests;

public class LspLanguageRowTests
{
  /// <summary>The plan's Language Rows table, restated as data: language id, display name,
  ///     install kind, prerequisite, extensions, manifests. The catalog is compared against
  ///     this and nothing else, so a row edited in one place and not the other fails here.</summary>
  private static readonly (string LanguageId, string DisplayName, LspInstallKind InstallKind,
      string? Prerequisite, string[] Extensions, string[] Manifests)[] PlanTable =
  [
      ("csharp", "C#", LspInstallKind.NuGet, null,
          [".cs"],
          [".sln", ".slnx", ".csproj"]),
      ("typescript", "TypeScript / JavaScript", LspInstallKind.Toolchain, "node",
          [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"],
          ["package.json", "tsconfig.json"]),
      ("python", "Python", LspInstallKind.Toolchain, "node",
          [".py", ".pyi"],
          ["pyproject.toml", "setup.py", "requirements.txt", "Pipfile"]),
      ("rust", "Rust", LspInstallKind.Archive, null,
          [".rs"],
          ["Cargo.toml"]),
      ("go", "Go", LspInstallKind.Toolchain, "go",
          [".go"],
          ["go.mod"]),
      ("java", "Java", LspInstallKind.Archive, "java",
          [".java"],
          ["pom.xml", "build.gradle", "build.gradle.kts"]),
      ("lua", "Lua", LspInstallKind.Archive, null,
          [".lua"],
          [".luarc.json"]),
      ("php", "PHP", LspInstallKind.Toolchain, "node",
          [".php"],
          ["composer.json"]),
      ("ruby", "Ruby", LspInstallKind.Toolchain, "gem",
          [".rb", ".rake"],
          ["Gemfile"]),
      ("bash", "Bash", LspInstallKind.Toolchain, "node",
          [".sh", ".bash"],
          []),
  ];

  [Fact]
  public void Catalog_CarriesExactlyThePlanTable()
  {
    Assert.Equal(PlanTable.Length, LspServerCatalog.All.Count);
    for (int index = 0; index < PlanTable.Length; index++)
    {
      LspLanguageRow row = LspServerCatalog.All[index];
      (string languageId, string displayName, LspInstallKind installKind, string? prerequisite,
          string[] extensions, string[] manifests) = PlanTable[index];
      Assert.Equal(languageId, row.LanguageId);
      Assert.Equal(displayName, row.DisplayName);
      Assert.Equal(installKind, row.InstallKind);
      Assert.Equal(prerequisite, row.PrerequisiteExecutable);
      Assert.Equal(extensions, row.Extensions);
      Assert.Equal(manifests, row.ManifestNames);
    }
  }

  [Fact]
  public void Catalog_EveryRowCarriesAPinAndAServerName()
  {
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      Assert.False(string.IsNullOrWhiteSpace(row.PackageId), row.LanguageId + " has no package id");
      Assert.False(string.IsNullOrWhiteSpace(row.Version), row.LanguageId + " has no version");
      Assert.False(string.IsNullOrWhiteSpace(row.ServerFileName), row.LanguageId + " has no server name");
    }
  }

  [Fact]
  public void Catalog_ARowThatDownloadsCarriesAChecksum()
  {
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      if (row.InstallKind == LspInstallKind.Toolchain)
      {
        Assert.Null(row.Checksum);
        Assert.Equal(string.Empty, row.EntryPointRelativePath);
        continue;
      }

      Assert.False(string.IsNullOrWhiteSpace(row.Checksum), row.LanguageId + " has no checksum");
    }
  }

  [Fact]
  public void Catalog_NuGetRowCarriesABase64Sha512()
  {
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      if (row.InstallKind != LspInstallKind.NuGet)
      {
        continue;
      }

      Assert.Equal(88, row.Checksum!.Length);
      Assert.EndsWith("==", row.Checksum, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void Catalog_ArchiveRowCarriesALowercaseHexSha256()
  {
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      if (row.InstallKind != LspInstallKind.Archive)
      {
        continue;
      }

      Assert.Equal(64, row.Checksum!.Length);
      Assert.All(row.Checksum, character => Assert.True(char.IsAsciiDigit(character) || (character >= 'a' && character <= 'f'), row.LanguageId));
    }
  }

  [Fact]
  public void Catalog_EveryExtensionIsDotPrefixedAndEveryRowHasOne()
  {
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      Assert.NotEmpty(row.Extensions);
      Assert.All(row.Extensions, extension => Assert.StartsWith(".", extension, StringComparison.Ordinal));
    }
  }

  [Fact]
  public void Catalog_NoExtensionIsClaimedByTwoRows()
  {
    HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      foreach (string extension in row.Extensions)
      {
        Assert.True(seen.Add(extension), extension + " is claimed by more than one row");
      }
    }
  }

  [Fact]
  public void Catalog_LanguageIdsAreUnique()
  {
    HashSet<string> ids = new(StringComparer.Ordinal);
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      Assert.True(ids.Add(row.LanguageId), row.LanguageId + " appears twice");
    }
  }

  [Fact]
  public void ForLanguage_IsCaseInsensitive()
  {
    LspLanguageRow? row = LspServerCatalog.ForLanguage("RUST");
    Assert.NotNull(row);
    Assert.Equal("rust", row.LanguageId);
    Assert.Equal("Archive", row.InstallKind.ToString());
  }

  [Fact]
  public void ForLanguage_NullOrUnknown_ReturnsNull()
  {
    Assert.Null(LspServerCatalog.ForLanguage(null));
    Assert.Null(LspServerCatalog.ForLanguage(""));
    Assert.Null(LspServerCatalog.ForLanguage("   "));
    Assert.Null(LspServerCatalog.ForLanguage("cobol"));
  }

  [Fact]
  public void ForLanguage_ResolvesEveryRowById()
  {
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      Assert.Same(row, LspServerCatalog.ForLanguage(row.LanguageId));
    }
  }

  [Fact]
  public void Claims_MatchesExtensionCaseInsensitively()
  {
    LspLanguageRow rust = LspServerCatalog.ForLanguage("rust")!;
    Assert.True(rust.Claims("src/main.rs"));
    Assert.True(rust.Claims("SRC/MAIN.RS"));
    Assert.False(rust.Claims("src/main.py"));
    Assert.False(rust.Claims("Cargo"));
  }

  [Fact]
  public void HasManifestName_MatchesTheFileNameOnly()
  {
    LspLanguageRow rust = LspServerCatalog.ForLanguage("rust")!;
    Assert.True(rust.HasManifestName("Cargo.toml"));
    Assert.True(rust.HasManifestName("cargo.TOML"));
    Assert.False(rust.HasManifestName("Cargo.lock"));
  }

  [Fact]
  public void InstallKind_HasExactlyThreeMembers() =>
      Assert.Equal(["NuGet", "Archive", "Toolchain"], Enum.GetNames<LspInstallKind>());
}
