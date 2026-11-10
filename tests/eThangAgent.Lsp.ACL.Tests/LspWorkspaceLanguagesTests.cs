using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

public class LspWorkspaceLanguagesTests
{
  private static string NewWorkspace() => TestTempRoot.CreateDirectory("wslang").FullName;

  private static void Touch(string root, string relativePath)
  {
    string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, "x");
  }

  [Fact]
  public void CargoTomlWithSources_ReportsRustWithBoth()
  {
    string root = NewWorkspace();
    try
    {
      Touch(root, "Cargo.toml");
      for (int index = 0; index < 40; index++)
      {
        Touch(root, "src/file" + index + ".rs");
      }

      DetectedLanguage only = Assert.Single(LspWorkspaceLanguages.Detect(root));
      Assert.Equal(new DetectedLanguage("rust", 40, true), only);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void PythonFileWithoutManifest_ReportsNoManifest()
  {
    string root = NewWorkspace();
    try
    {
      Touch(root, "src/a.py");
      DetectedLanguage only = Assert.Single(LspWorkspaceLanguages.Detect(root));
      Assert.Equal(new DetectedLanguage("python", 1, false), only);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void TypeScriptFiles_AreNotAlsoCountedAsJavaScript()
  {
    string root = NewWorkspace();
    try
    {
      Touch(root, "package.json");
      Touch(root, "a.ts");
      Touch(root, "b.ts");
      Touch(root, "c.ts");
      DetectedLanguage only = Assert.Single(LspWorkspaceLanguages.Detect(root));
      Assert.Equal(new DetectedLanguage("typescript", 3, true), only);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void JavaScriptFiles_AreServedByTheTypeScriptRow()
  {
    string root = NewWorkspace();
    try
    {
      Touch(root, "a.js");
      Touch(root, "b.jsx");
      DetectedLanguage only = Assert.Single(LspWorkspaceLanguages.Detect(root));
      Assert.Equal(new DetectedLanguage("typescript", 2, false), only);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void UnclaimedFiles_ReportNothing()
  {
    string root = NewWorkspace();
    try
    {
      Touch(root, "README.md");
      Touch(root, "notes.txt");
      Assert.Empty(LspWorkspaceLanguages.Detect(root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void ManifestOnly_ReportsZeroFiles()
  {
    string root = NewWorkspace();
    try
    {
      Touch(root, "go.mod");
      DetectedLanguage only = Assert.Single(LspWorkspaceLanguages.Detect(root));
      Assert.Equal(new DetectedLanguage("go", 0, true), only);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void ManifestBelowTheRoot_IsFound()
  {
    string root = NewWorkspace();
    try
    {
      Touch(root, "src/nested/Cargo.toml");
      DetectedLanguage only = Assert.Single(LspWorkspaceLanguages.Detect(root));
      Assert.Equal(new DetectedLanguage("rust", 0, true), only);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void TwoManifests_AreOrderedByCountThenId()
  {
    string root = NewWorkspace();
    try
    {
      Touch(root, "Cargo.toml");
      Touch(root, "go.mod");
      IReadOnlyList<DetectedLanguage> detected = LspWorkspaceLanguages.Detect(root);
      Assert.Equal(
          [new DetectedLanguage("go", 0, true), new DetectedLanguage("rust", 0, true)],
          detected);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void ManifestPresence_OutranksFileCount()
  {
    string root = NewWorkspace();
    try
    {
      Touch(root, "a.py");
      Touch(root, "b.py");
      Touch(root, "Cargo.toml");
      IReadOnlyList<DetectedLanguage> detected = LspWorkspaceLanguages.Detect(root);
      Assert.Equal(
          [new DetectedLanguage("rust", 0, true), new DetectedLanguage("python", 2, false)],
          detected);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void HigherFileCount_OutranksLowerWithoutManifest()
  {
    string root = NewWorkspace();
    try
    {
      Touch(root, "a.py");
      Touch(root, "a.go");
      Touch(root, "b.go");
      IReadOnlyList<DetectedLanguage> detected = LspWorkspaceLanguages.Detect(root);
      Assert.Equal(
          [new DetectedLanguage("go", 2, false), new DetectedLanguage("python", 1, false)],
          detected);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void MissingRoot_ReturnsEmpty() =>
      Assert.Empty(LspWorkspaceLanguages.Detect(Path.Combine(TestTempRoot.Root, "no-such-workspace-xyz")));

  [Fact]
  public void ExcludedDirectories_AreNotWalked()
  {
    string root = NewWorkspace();
    try
    {
      Touch(root, "node_modules/pkg/index.js");
      Touch(root, "bin/Debug/a.cs");
      Touch(root, "obj/a.cs");
      Touch(root, ".venv/lib/a.py");
      Assert.Empty(LspWorkspaceLanguages.Detect(root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void WalkBeyondTheFileBudget_ReportsOnlyWhatItReached()
  {
    string root = NewWorkspace();
    try
    {
      for (int index = 0; index < 2100; index++)
      {
        Touch(root, "filler/file" + index + ".txt");
      }

      Touch(root, "zzz/late.rs");
      IReadOnlyList<DetectedLanguage> detected = LspWorkspaceLanguages.Detect(root);
      Assert.DoesNotContain(detected, entry => entry.LanguageId == "rust");
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void EveryRowIsReachableByItsManifest()
  {
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      if (row.ManifestNames.Count == 0)
      {
        continue;
      }

      string root = NewWorkspace();
      try
      {
        Touch(root, row.ManifestNames[0]);
        IReadOnlyList<DetectedLanguage> detected = LspWorkspaceLanguages.Detect(root);
        DetectedLanguage entry = Assert.Single(detected);
        Assert.Equal(row.LanguageId, entry.LanguageId);
        Assert.True(entry.HasManifest);
        Assert.Equal(0, entry.FileCount);
      }
      finally { Directory.Delete(root, true); }
    }
  }

  [Fact]
  public void EveryRowIsReachableByItsExtension()
  {
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      string root = NewWorkspace();
      try
      {
        Touch(root, "sample" + row.Extensions[0]);
        IReadOnlyList<DetectedLanguage> detected = LspWorkspaceLanguages.Detect(root);
        DetectedLanguage entry = Assert.Single(detected);
        Assert.Equal(row.LanguageId, entry.LanguageId);
        Assert.Equal(1, entry.FileCount);
        Assert.False(entry.HasManifest);
      }
      finally { Directory.Delete(root, true); }
    }
  }
}
