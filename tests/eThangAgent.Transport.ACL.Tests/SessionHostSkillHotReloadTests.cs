using eThangAgent.ChildHost;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp cleanup in catch blocks is deliberate (CA1031), matching the
// sibling host tests.
#pragma warning disable CA1031 // Do not catch general exception types

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Hot reload in the container that ACTUALLY serves the model (issue #269).
///     <para>Every production conversation runs its turns in a ChildHost process, and that
///     process builds its own container through <see cref="SessionHost.Create"/> - it never
///     reaches the app's <c>AgentSessionFactory.BuildSession</c>, which was the ONLY caller
///     of <c>SkillDirectoryWatcher.Start()</c>. The watcher was therefore registered and
///     INERT in every host container: a skill added while a session was open stayed
///     invisible for the session's whole lifetime, because the composite catalog memoizes
///     its first load and nothing ever reloaded it.</para>
///     <para>The app-side assertion that existed (<c>SkillDirectoryWatcherWiringTests</c>)
///     could not see this: it inspects the app's container, not the host's. These tests
///     assert liveness on the HOST container specifically.</para></summary>
public class SessionHostSkillHotReloadTests
{
  [Fact]
  public async Task HostContainer_StartsWithTheWatcher_AndSeesASkillWrittenAfterBuild()
  {
    string skillsRoot = Directory.CreateTempSubdirectory("ethang-host-skills").FullName;
    string settingsPath = Path.Combine(Path.GetTempPath(),
        "ethang-host-skills-" + Guid.NewGuid().ToString("N") + ".json");
    string dbPath = Path.Combine(Path.GetTempPath(),
        "ethang-host-skills-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
      WriteSettings(settingsPath, skillsRoot);
      SessionHost host = SessionHost.Create(settingsPath, dbPath);
      try
      {
        SkillDirectoryWatcher watcher = (SkillDirectoryWatcher)host.Services
            .GetRequiredService(typeof(SkillDirectoryWatcher));

        // THE defect: the host container registered the watcher but never started it.
        Assert.True(watcher.IsStarted,
            "the conversation host's skill watcher must be started; an inert watcher means a "
            + "skill added mid-session is invisible for the session's whole lifetime.");

        // And the observable consequence: a skill written AFTER the container was built
        // must become visible to the catalog once a reload pass runs.
        string skillDir = Path.Combine(skillsRoot, "late-skill");
        _ = Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: late-skill\ndescription: written after the host container started\n---\n\nBody.\n",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        ISkillCatalog catalog = (ISkillCatalog)host.Services.GetRequiredService(typeof(ISkillCatalog));
        for (int attempt = 0; 5 > attempt; attempt++)
        {
          await watcher.CheckOnceAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
          Result<IReadOnlyList<SkillDefinition>> listed =
              await catalog.ListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
          if (listed.IsSuccess && listed.Value.Any(s => s.Name == "late-skill"))
          {
            break;
          }
        }

        Result<IReadOnlyList<SkillDefinition>> final =
            await catalog.ListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(final.IsSuccess);
        Assert.Contains("late-skill", final.Value.Select(s => s.Name));
      }
      finally
      {
        await DisposeHostAsync(host).ConfigureAwait(true);
      }
    }
    finally
    {
      Delete(settingsPath);
      Delete(dbPath);
      Delete(skillsRoot, recursive: true);
    }
  }

  /// <summary>The announcement's SECOND channel (issue #269, secondary defect): the
  ///     reloader posts the rendered line to the session notice sink, which used to be
  ///     wired as <c>sp.GetRequiredService&lt;AgentSession&gt;()</c> - a type registered in
  ///     NO container - so every post threw and <c>Announce</c> swallowed it under its
  ///     best-effort contract. The transcript notice therefore never appeared, silently.
  ///     The container's <see cref="SessionNoticeSink"/> carrier is the resolvable seam.</summary>
  [Fact]
  public async Task HostContainer_ReloadAnnouncement_ReachesTheNoticeSink()
  {
    string skillsRoot = Directory.CreateTempSubdirectory("ethang-host-notice").FullName;
    string settingsPath = Path.Combine(Path.GetTempPath(),
        "ethang-host-notice-" + Guid.NewGuid().ToString("N") + ".json");
    string dbPath = Path.Combine(Path.GetTempPath(),
        "ethang-host-notice-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
      WriteSettings(settingsPath, skillsRoot);
      SessionHost host = SessionHost.Create(settingsPath, dbPath);
      try
      {
        // The shell's attach shape: the carrier is filled once a transcript exists.
        SessionNoticeSink notices = (SessionNoticeSink)host.Services
            .GetRequiredService(typeof(SessionNoticeSink));
        List<string> posted = [];
        notices.Sink = message =>
        {
          lock (posted)
          {
            posted.Add(message);
          }
        };

        string skillDir = Path.Combine(skillsRoot, "announced-skill");
        _ = Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: announced-skill\ndescription: announced through the notice sink\n---\n\nBody.\n",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        SkillDirectoryWatcher watcher = (SkillDirectoryWatcher)host.Services
            .GetRequiredService(typeof(SkillDirectoryWatcher));
        await watcher.CheckOnceAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        string? line;
        lock (posted)
        {
          line = posted.FirstOrDefault(m => m.StartsWith("[skills reloaded]", StringComparison.Ordinal));
        }

        Assert.NotNull(line);
        Assert.Contains("announced-skill", line, StringComparison.Ordinal);
      }
      finally
      {
        await DisposeHostAsync(host).ConfigureAwait(true);
      }
    }
    finally
    {
      Delete(settingsPath);
      Delete(dbPath);
      Delete(skillsRoot, recursive: true);
    }
  }

  /// <summary>The settings JSON the supervisor writes: the skill-directory preference
  ///     lists ride it verbatim, so the host resolves the SAME directories the app did.</summary>
  private static void WriteSettings(string path, string skillsRoot)
  {
    string directories = "[{\"path\":" + System.Text.Json.JsonSerializer.Serialize(skillsRoot)
        + ",\"enabled\":true}]";
    File.WriteAllText(path,
        "{\"OpenRouter\":{\"ApiKey\":\"sk-or-probe\",\"BaseUrl\":\"https://openrouter.invalid/api/v1\"},"
        + "\"SubAgents\":{},"
        + "\"WorkspaceRoot\":" + System.Text.Json.JsonSerializer.Serialize(skillsRoot) + ","
        + "\"SkillDirectoriesGlobal\":" + System.Text.Json.JsonSerializer.Serialize(directories)
        + "}");
  }

  private static async ValueTask DisposeHostAsync(SessionHost host)
  {
    if (host.Services is IAsyncDisposable asyncDisposable)
    {
      await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }
    else if (host.Services is IDisposable disposable)
    {
      disposable.Dispose();
    }
  }

  private static void Delete(string path, bool recursive = false)
  {
    try
    {
      if (Directory.Exists(path))
      {
        Directory.Delete(path, recursive);
      }
      else
      {
        File.Delete(path);
      }
    }
    catch
    {
      // best effort
    }
  }
}
