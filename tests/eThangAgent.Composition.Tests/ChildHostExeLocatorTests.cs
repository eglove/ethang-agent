namespace eThangAgent.Composition.Tests;

/// <summary>ChildHost exe resolution must find the host build output no matter which
///     configuration the caller runs under: local dev builds Debug, while CI (and any
///     release-style invocation) builds Release only. The Debug-only assumption left
///     master red for days - every remote-host E2E failed with "child host exe not
///     built" on CI while passing on dev machines (run 33987882753).</summary>
public class ChildHostExeLocatorTests
{
  private const string ExeName = "eThangAgent.ChildHost.exe";

  private static string MakeFakeRepo()
  {
    string root = Path.Combine(Path.GetTempPath(), "childhost-locator-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(Path.Combine(root, "src", "eThangAgent.ChildHost", "bin"));
    return root;
  }

  private static void PlantExe(string repoRoot, string configuration)
  {
    string dir = Path.Combine(repoRoot, "src", "eThangAgent.ChildHost", "bin", configuration, "net10.0");
    _ = Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, ExeName), string.Empty);
  }

  [Fact]
  public void ResolveFromRepoRoot_Finds_The_Debug_Output()
  {
    string repo = MakeFakeRepo();
    try
    {
      PlantExe(repo, "Debug");

      string resolved = ChildHostExeLocator.ResolveFromRepoRoot(repo);

      Assert.EndsWith(Path.Combine("bin", "Debug", "net10.0", ExeName), resolved, StringComparison.Ordinal);
      Assert.True(File.Exists(resolved));
    }
    finally
    {
      Directory.Delete(repo, recursive: true);
    }
  }

  [Fact]
  public void ResolveFromRepoRoot_Falls_Back_To_The_Release_Output()
  {
    string repo = MakeFakeRepo();
    try
    {
      PlantExe(repo, "Release");

      string resolved = ChildHostExeLocator.ResolveFromRepoRoot(repo);

      Assert.EndsWith(Path.Combine("bin", "Release", "net10.0", ExeName), resolved, StringComparison.Ordinal);
      Assert.True(File.Exists(resolved));
    }
    finally
    {
      Directory.Delete(repo, recursive: true);
    }
  }

  [Fact]
  public void ResolveFromRepoRoot_Prefers_Debug_When_Both_Configurations_Exist()
  {
    string repo = MakeFakeRepo();
    try
    {
      PlantExe(repo, "Debug");
      PlantExe(repo, "Release");

      string resolved = ChildHostExeLocator.ResolveFromRepoRoot(repo);

      Assert.EndsWith(Path.Combine("bin", "Debug", "net10.0", ExeName), resolved, StringComparison.Ordinal);
    }
    finally
    {
      Directory.Delete(repo, recursive: true);
    }
  }
}
