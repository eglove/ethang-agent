using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>End-to-end hot reload (spec #26): a real directory under watch; a
/// SKILL.md written after Start shows up in the catalog after a reload pass and
/// the announcement line lands on both channels. Uses CheckOnceAsync (the same
/// entry point the FSW debounce and the sweep call), so no timing assertions.</summary>
public class SkillDirectoryReloadE2ETests
{
  [Fact]
  public async Task WriteThenReload_NewSkillVisibleAndAnnounced()
  {
    string dir = Directory.CreateTempSubdirectory("ethang-hotreload").FullName;
    try
    {
      FakeBuiltIn builtIns = new();
      CompositeSkillCatalog catalog = new(builtIns, new FileSystem.ACL.DirectorySkillSource(),
          [new SkillDirectory(dir, SkillDirectoryScope.Global)]);
      Conversation conversation = new();
      string? notice = null;
      SkillDirectoryReloader reloader = new(() => conversation, m => notice = m);
      SkillDirectoryWatcher watcher = new(catalog, reloader.Announce,
          [new SkillDirectory(dir, SkillDirectoryScope.Global)]);
      watcher.Start();
      try
      {
        string skillDir = Path.Combine(dir, "alpha");
        _ = Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: alpha\ndescription: E2E reload proof skill\n---\n\nBody.\n",
            TestContext.Current.CancellationToken);

        // Polling reload passes are cheap; up to 5 until the write is visible
        // (DirectorySkillSource may lag the filesystem by a moment).
        for (int attempt = 0; 5 > attempt; attempt++)
        {
          await watcher.CheckOnceAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
          Result<IReadOnlyList<SkillDefinition>> listed =
              await catalog.ListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
          if (listed.IsSuccess && listed.Value.Any(s => s.Name == "alpha"))
          {
            break;
          }
        }

        Result<IReadOnlyList<SkillDefinition>> final =
            await catalog.ListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(final.IsSuccess);
        Assert.Contains("alpha", final.Value.Select(s => s.Name));
        Assert.NotNull(notice);
        Assert.StartsWith("[skills reloaded] + alpha: ", notice, StringComparison.Ordinal);
        _ = Assert.Single(conversation.Messages, m => m.Content.StartsWith("[skills reloaded]", StringComparison.Ordinal));
      }
      finally
      {
        await watcher.DisposeAsync().ConfigureAwait(true);
      }
    }
    finally
    {
      try
      {
        Directory.Delete(dir, true);
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        // Best-effort temp cleanup (named decision, mirrors sibling tests).
      }
    }
  }

  private sealed class FakeBuiltIn : ISkillCatalog
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([]));
    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<SkillDefinition>(new DomainError("x", "x")));
  }
}
