// E2E fixture: real git clone from a local fixture repo, real FileSystem ACL
// adapter, real hot-reload watcher; sync IO and best-effort cleanup deliberate.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Skill registry end-to-end (plan #29 task 13): install from a local
///     fixture repo through the real adapters into a temp configured directory;
///     the hot-reload watcher announces the addition on both channels; a second
///     install without force fails NameCollision.</summary>
public class SkillRegistryE2ETests
{
  [Fact]
  public async Task Install_FixtureRepo_Installs_And_Announces()
  {
    string repo = CreateFixtureRepo();
    string target = Directory.CreateTempSubdirectory("ethang-regtarget").FullName;
    string stagingParent = Directory.CreateTempSubdirectory("ethang-regstage").FullName;
    try
    {
      GitSkillRegistryAccess registryAccess = new();
      FakeSkillsShAccess skillsSh = new();
      CompositeSkillCatalog catalog = new(new EmptyBuiltIns(), new DirectorySkillSource(),
          [new SkillDirectory(target, SkillDirectoryScope.Workspace)]);
      Conversation conversation = new();
      string? notice = null;
      SkillDirectoryReloader reloader = new(() => conversation, m => notice = m);
      SkillDirectoryWatcher watcher = new(catalog, reloader.Announce,
          [new SkillDirectory(target, SkillDirectoryScope.Workspace)]);
      SkillRegistryService service = new(
          registryAccess, skillsSh, catalog,
          () => "workspace",
          () => [target]);
      watcher.Start();
      try
      {
        Result<SkillAddress> address = SkillAddress.Create(new Uri(repo).ToString());
        Assert.True(address.IsSuccess);
        Result<SkillInstallReport> r = await service.InstallAsync(
            address.Value, "workspace", false, false, TestContext.Current.CancellationToken);
        Assert.True(r.IsSuccess, "install failed: [" + r.Error?.Code + "] " + r.Error?.Message + " address=[" + new Uri(repo).ToString() + "]");
        Assert.Equal(["deploy-helper"], r.Value.InstalledNames);
        Assert.True(File.Exists(Path.Combine(target, "deploy-helper", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(target, "deploy-helper", "references", "guide.md")));

        // Timing-tolerant polling (spec #26 pattern): the catalog's own reload is
        // the deterministic entry point; the reloader announces the diff on both
        // channels. The watcher stays running as the asynchronous net.
        bool visible = false;
        for (int attempt = 0; 10 > attempt && !visible; attempt++)
        {
          Result<SkillReloadDiff> diff = await catalog.ReloadAsync(TestContext.Current.CancellationToken);
          if (diff.IsSuccess && diff.Value.Added.Count > 0)
          {
            reloader.Announce(diff.Value);
          }

          Result<IReadOnlyList<SkillDefinition>> listed = await catalog.ListAsync(TestContext.Current.CancellationToken);
          visible = listed.IsSuccess && listed.Value.Any(s => s.Name == "deploy-helper");
        }

        Assert.True(visible, "installed skill never became visible in the catalog");
        Assert.NotNull(notice);
        Assert.StartsWith("[skills reloaded] + deploy-helper: ", notice, StringComparison.Ordinal);
        int announced = conversation.Messages.Count(m => m.Content.StartsWith("[skills reloaded]", StringComparison.Ordinal));
        Assert.Equal(1, announced);

        // Second install of the same address without force refuses.
        Result<SkillInstallReport> again = await service.InstallAsync(
            address.Value, "workspace", false, false, TestContext.Current.CancellationToken);
        Assert.False(again.IsSuccess);
        Assert.Equal("NameCollision", again.Error.Code);
      }
      finally
      {
        await watcher.DisposeAsync();
      }
    }
    finally
    {
      TryDelete(repo);
      TryDelete(target);
      TryDelete(stagingParent);
    }
  }

  private static string CreateFixtureRepo()
  {
    string repo = Directory.CreateTempSubdirectory("ethang-regrepo").FullName;
    RunGit(repo, "init", "-b", "main");
    RunGit(repo, "config", "user.email", "test@example.com");
    RunGit(repo, "config", "user.name", "Test");
    _ = Directory.CreateDirectory(Path.Combine(repo, "skills", "deploy-helper", "references"));
    File.WriteAllText(Path.Combine(repo, "skills", "deploy-helper", "SKILL.md"),
        "---\nname: deploy-helper\ndescription: Deploys the service when the user asks to deploy.\n---\nUse when deploying.");
    File.WriteAllText(Path.Combine(repo, "skills", "deploy-helper", "references", "guide.md"), "# guide");
    RunGit(repo, "add", ".");
    RunGit(repo, "commit", "-m", "fixture");
    return repo;
  }

  private static void RunGit(string workdir, params string[] args)
  {
    System.Diagnostics.ProcessStartInfo psi = new("git")
    {
      WorkingDirectory = workdir,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (string a in args)
    {
      psi.ArgumentList.Add(a);
    }

    using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
    string stdout = p.StandardOutput.ReadToEnd();
    string stderr = p.StandardError.ReadToEnd();
    bool exited = p.WaitForExit(30000);
    _ = stdout + stderr + exited;
  }

  private static void TryDelete(string dir)
  {
    try
    {
      Directory.Delete(dir, true);
    }
    catch (IOException)
    {
      // best-effort temp cleanup
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort temp cleanup
    }
  }

  private sealed class EmptyBuiltIns : ISkillCatalog
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([]));

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<SkillDefinition>(new DomainError("SkillNotFound", name)));
  }

  /// <summary>The local fixture path travels as a plain address; no skills.sh fetch.</summary>
  private sealed class FakeSkillsShAccess : ISkillsShAccess
  {
    public Task<Result<string>> FetchSearchAsync(string query, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<string>(new DomainError("FetchFailed", "not used in this test")));
  }
}
