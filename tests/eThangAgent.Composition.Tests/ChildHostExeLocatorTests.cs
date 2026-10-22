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

  private static void PlantWindowsExe(string repoRoot, string configuration)
  {
    string dir = Path.Combine(repoRoot, "src", "eThangAgent.ChildHost", "bin", configuration, "net10.0-windows");
    _ = Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, ExeName), string.Empty);
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
  [Fact]
  public void ResolveFromRepoRoot_Finds_The_WindowsTfm_Output()
  {
    // The ChildHost project targets net10.0-windows; its build output lands in
    // bin/<config>/net10.0-windows/. A locator that probes only net10.0 fails on
    // every fresh checkout and worktree (the follow-up pass's environmental blocker).
    string repo = MakeFakeRepo();
    try
    {
      PlantWindowsExe(repo, "Debug");

      string resolved = ChildHostExeLocator.ResolveFromRepoRoot(repo);

      Assert.EndsWith(Path.Combine("bin", "Debug", "net10.0-windows", ExeName), resolved, StringComparison.Ordinal);
      Assert.True(File.Exists(resolved));
    }
    finally
    {
      Directory.Delete(repo, recursive: true);
    }
  }

  [Fact]
  public void ResolveFromRepoRoot_Prefers_WindowsTfm_Over_PlainTfm_StaleFolder()
  {
    // A stale pre-TFM-bump net10.0 folder must not win over the live output.
    string repo = MakeFakeRepo();
    try
    {
      PlantExe(repo, "Debug");          // stale-shape folder
      PlantWindowsExe(repo, "Debug");   // the real output

      string resolved = ChildHostExeLocator.ResolveFromRepoRoot(repo);

      Assert.EndsWith(Path.Combine("bin", "Debug", "net10.0-windows", ExeName), resolved, StringComparison.Ordinal);
    }
    finally
    {
      Directory.Delete(repo, recursive: true);
    }
  }

  [Fact]
  public void ResolveFromRepoRoot_Falls_Back_To_The_WindowsTfm_Release_Output()
  {
    string repo = MakeFakeRepo();
    try
    {
      PlantWindowsExe(repo, "Release");

      string resolved = ChildHostExeLocator.ResolveFromRepoRoot(repo);

      Assert.EndsWith(Path.Combine("bin", "Release", "net10.0-windows", ExeName), resolved, StringComparison.Ordinal);
      Assert.True(File.Exists(resolved));
    }
    finally
    {
      Directory.Delete(repo, recursive: true);
    }
  }

  // ── the DEPLOYED layout: the host ships NEXT TO THE APP ──

  [Fact]
  public void Resolve_Without_A_Repo_Resolves_The_Host_Shipped_Next_To_The_App()
  {
    // A published app has NO repo above it: the 2026-10-21 crash walked past the
    // filesystem root, found nothing, and dereferenced the null.
    string appDir = Directory.CreateTempSubdirectory("ethang-deployed-app").FullName;
    try
    {
      string resolved = ChildHostExeLocator.Resolve(repoRoot: null, appDir);

      Assert.Equal(Path.Combine(appDir, ExeName), resolved);
    }
    finally
    {
      Directory.Delete(appDir, recursive: true);
    }
  }

  [Fact]
  public void Resolve_Prefers_The_Repo_Build_Output_When_It_Exists()
  {
    // The dev layout: what the developer last built is the freshest host.
    string repo = MakeFakeRepo();
    string appDir = Directory.CreateTempSubdirectory("ethang-deployed-app").FullName;
    try
    {
      PlantWindowsExe(repo, "Debug");

      string resolved = ChildHostExeLocator.Resolve(repo, appDir);

      Assert.Equal(
          Path.Combine(repo, "src", "eThangAgent.ChildHost", "bin", "Debug", "net10.0-windows", ExeName),
          resolved);
    }
    finally
    {
      Directory.Delete(repo, recursive: true);
      Directory.Delete(appDir, recursive: true);
    }
  }

  [Fact]
  public void Resolve_Falls_Back_To_The_App_Directory_When_The_Repo_Has_No_Built_Host()
  {
    // The repo is there but the host was never built in it: the copy shipped with
    // the app is the answer, not a not-found path.
    string repo = MakeFakeRepo();
    string appDir = Directory.CreateTempSubdirectory("ethang-deployed-app").FullName;
    try
    {
      string resolved = ChildHostExeLocator.Resolve(repo, appDir);

      Assert.Equal(Path.Combine(appDir, ExeName), resolved);
    }
    finally
    {
      Directory.Delete(repo, recursive: true);
      Directory.Delete(appDir, recursive: true);
    }
  }
}
