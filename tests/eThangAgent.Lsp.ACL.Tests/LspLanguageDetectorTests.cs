using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

public class LspLanguageDetectorTests
{
  [Fact]
  public void Sln_DetectsCSharp()
  {
    string root = Directory.CreateTempSubdirectory("det").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "app.sln"), "x");
      Assert.Equal("csharp", LspLanguageDetector.DetectLanguage(root));
      Assert.True(LspLanguageDetector.HasProjectFile(root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void Csproj_DetectsCSharp()
  {
    string root = Directory.CreateTempSubdirectory("det").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "app.csproj"), "x");
      Assert.Equal("csharp", LspLanguageDetector.DetectLanguage(root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void Slnx_DetectsCSharp()
  {
    string root = Directory.CreateTempSubdirectory("det").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "app.slnx"), "x");
      Assert.Equal("csharp", LspLanguageDetector.DetectLanguage(root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void ProjectLess_ReturnsNull()
  {
    string root = Directory.CreateTempSubdirectory("det").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "notes.md"), "hello");
      Assert.Null(LspLanguageDetector.DetectLanguage(root));
      Assert.False(LspLanguageDetector.HasProjectFile(root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void MissingRoot_ReturnsNull() =>
    Assert.Null(LspLanguageDetector.DetectLanguage(Path.Combine(Path.GetTempPath(), "no-such-dir-xyz")));
}
