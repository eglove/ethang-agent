using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

public class LspLanguageRouterTests
{
  [Theory]
  [InlineData("a.cs", "csharp")]
  [InlineData("src/Program.cs", "csharp")]
  [InlineData("a.rs", "rust")]
  [InlineData("a.ts", "typescript")]
  [InlineData("a.tsx", "typescript")]
  [InlineData("a.js", "typescript")]
  [InlineData("a.mjs", "typescript")]
  [InlineData("a.py", "python")]
  [InlineData("a.go", "go")]
  [InlineData("a.java", "java")]
  [InlineData("a.lua", "lua")]
  [InlineData("a.php", "php")]
  [InlineData("a.rb", "ruby")]
  [InlineData("a.sh", "bash")]
  public void ForPath_ReturnsTheLanguageWhoseRowClaimsTheExtension(string path, string expected)
    => Assert.Equal(expected, LspLanguageRouter.ForPath(path));

  [Theory]
  [InlineData("notes.md")]
  [InlineData("Makefile")]
  [InlineData("Cargo.toml")]
  [InlineData("src/data.json")]
  public void ForPath_NoRowClaimsTheExtension_ReturnsNull(string path)
    => Assert.Null(LspLanguageRouter.ForPath(path));

  /// <summary>Every extension the catalog records routes to the row that records it, so a
  ///     row added or an extension moved cannot leave a file unroutable.</summary>
  [Fact]
  public void ForPath_EveryCatalogExtension_RoutesToItsOwnRow()
  {
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      foreach (string extension in row.Extensions)
      {
        Assert.Equal(row.LanguageId, LspLanguageRouter.ForPath("probe" + extension));
      }
    }
  }

  [Fact]
  public void ForPath_NullPath_Throws()
    => Assert.Throws<ArgumentNullException>(() => LspLanguageRouter.ForPath(null!));
}
