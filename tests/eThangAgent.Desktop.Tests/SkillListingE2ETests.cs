using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>End-to-end proof of the always-on skills listing (skill-routing
///     Phase 2, Task 12): the listing provider is registered at BOTH composition
///     sites — the app container and the remote ChildHost container — so every
///     session prompt carries the budgeted listing block between the skills
///     bootstrap and the persona line. Assertions use the container-resolution
///     form: the composed SystemPrompt is rendered by the production factory
///     open path over the SAME app database the preferences live in. The
///     bootstrap mandate still injects (phase 3 de-mandates it); coexistence
///     here is the intended phase shape.</summary>
[Collection("Desktop E2E")]
public class SkillListingE2ETests
{
  private const string ListingHeader =
      "[skills listing — prefer a matching skill over improvising; load bodies with skill_view]";
  private const string BuiltInGroupName = "## Built-in";
  private const string LearnedGroupName = "## Learned";
  private const string GlobalGroupName = "## Global directory skills";
  private const string WorkspaceGroupName = "## Workspace directory skills";

  private static readonly string[] RequiredBuiltIns =
  [
      "brainstorming", "systematic-debugging", "test-driven-development", "using-skills",
  ];

  /// <summary>Asserts the listing sits BETWEEN the skills bootstrap and the
  ///     persona line: after the bootstrap's already-active notice, before the
  ///     'You are eThang Agent' persona sentence (Task 12's positional pin).</summary>
  private static void AssertListingSitsBetweenBootstrapAndPersona(string prompt)
  {
    int bootstrapNotice = prompt.IndexOf("ALREADY ACTIVE", StringComparison.Ordinal);
    int listing = prompt.IndexOf(ListingHeader, StringComparison.Ordinal);
    int persona = prompt.IndexOf("You are eThang Agent", StringComparison.Ordinal);
    Assert.True(bootstrapNotice >= 0, "bootstrap notice missing from the prompt");
    Assert.True(listing > bootstrapNotice,
        $"listing ({listing}) must come after the skills bootstrap ({bootstrapNotice})");
    Assert.True(persona > listing,
        $"persona line ({persona}) must come after the listing ({listing})");
  }

  /// <summary>Opens a session through the REAL production factory over the SAME
  ///     database the harness runs on, and returns its composed SystemPrompt.
  ///     The harness's own bootstrap model pin keeps selection from running, so
  ///     factory opens resolve without any scripted chat response.</summary>
  private static async Task<string> OpenedSystemPromptAsync(E2E.HostHarness host, string workspaceRoot)
  {
    AgentSessionFactory factory = host.CreateResumeFactory();
    Result<AgentSession> opened = await factory.CreateAsync(
      workspaceRoot, Providers.OpenRouter, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(opened.IsSuccess);
    AgentSession session = opened.Value;
    string prompt = session.SystemPrompt;
    await session.Services.DisposeAsync().ConfigureAwait(true);
    return prompt;
  }

  [Fact]
  public async Task SystemPrompt_ListingRegistered_ExactHeaderBetweenBootstrapAndPersona()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-skilllist-ws");
    try
    {
      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName).ConfigureAwait(true);

      string prompt = await OpenedSystemPromptAsync(host, ws.FullName).ConfigureAwait(true);

      Assert.Contains(ListingHeader, prompt, StringComparison.Ordinal);
      AssertListingSitsBetweenBootstrapAndPersona(prompt);
    }
    finally
    {
      ws.Delete(true);
    }
  }

  [Fact]
  public async Task SystemPrompt_ListingContainsEveryBuiltInSkillName()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-skilllist-builtin");
    try
    {
      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName).ConfigureAwait(true);

      string prompt = await OpenedSystemPromptAsync(host, ws.FullName).ConfigureAwait(true);

      int listingStart = prompt.IndexOf(ListingHeader, StringComparison.Ordinal);
      Assert.True(listingStart >= 0, "listing header missing");
      int persona = prompt.IndexOf("You are eThang Agent", StringComparison.Ordinal);
      string listing = prompt[listingStart..(persona > listingStart ? persona : prompt.Length)];
      Assert.Contains(BuiltInGroupName, listing, StringComparison.Ordinal);
      foreach (string name in RequiredBuiltIns)
      {
        Assert.Contains(name, listing, StringComparison.Ordinal);
      }
    }
    finally
    {
      ws.Delete(true);
    }
  }

  [Fact]
  public async Task SystemPrompt_LearnedSkillNameListed_BodyTokenAbsent()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-skilllist-learned");
    try
    {
      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName).ConfigureAwait(true);

      // Seed ONE learned skill with a distinctive body token through the REAL
      // SQLite learned-skill store over the SAME app database the session reads.
      SqliteLearnedSkillStore learned = new(new AppDatabase(host.DatabasePath));
      const string token = "LEARNED-BODY-TOKEN-XQ71";
      Result<SkillDefinition> created = await learned.CreateAsync(new SkillDefinition(
        "e2e-learned-skill", "End-to-end learned skill for the listing test.",
        $"Body carrying {token} that must never reach the prompt.", 1, SkillSource.Learned,
        "session-1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(created.IsSuccess);

      string prompt = await OpenedSystemPromptAsync(host, ws.FullName).ConfigureAwait(true);

      int listingStart = prompt.IndexOf(ListingHeader, StringComparison.Ordinal);
      Assert.True(listingStart >= 0, "listing header missing");
      int persona = prompt.IndexOf("You are eThang Agent", StringComparison.Ordinal);
      string listing = prompt[listingStart..(persona > listingStart ? persona : prompt.Length)];
      Assert.Contains(LearnedGroupName, listing, StringComparison.Ordinal);
      Assert.Contains("e2e-learned-skill", listing, StringComparison.Ordinal);
      Assert.DoesNotContain(token, prompt, StringComparison.Ordinal);
    }
    finally
    {
      ws.Delete(true);
    }
  }

  [Fact]
  public async Task SystemPrompt_FileSkillAppearsUnderDirectoryHeader()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-skilllist-file-ws");
    DirectoryInfo skills = Directory.CreateTempSubdirectory("ethang-e2e-skilllist-dir");
    try
    {
      const string fileSkillName = "e2e-file-skill-listing";
      string skillFolder = Path.Combine(skills.FullName, fileSkillName);
      _ = Directory.CreateDirectory(skillFolder);
      string[] skillLines =
      [
        "---",
        $"name: {fileSkillName}",
        "description: File skill listed by the always-on session-start listing.",
        "---",
        "",
        $"Body of the {fileSkillName} file skill.",
        "",
      ];
      await File.WriteAllLinesAsync(Path.Combine(skillFolder, "SKILL.md"), skillLines,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName).ConfigureAwait(true);

      // The preference is written BEFORE the session opens, exactly the way the
      // settings surface persists it — the workspace key in the SAME app database.
      _ = await host.Store.SetAsync(
        SkillDirectoryPreferences.WorkspaceKey(ws.FullName),
        SkillDirectoryPreferences.Serialize([new SessionFileEntry(skills.FullName, Enabled: true)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

      string prompt = await OpenedSystemPromptAsync(host, ws.FullName).ConfigureAwait(true);

      int listingStart = prompt.IndexOf(ListingHeader, StringComparison.Ordinal);
      Assert.True(listingStart >= 0, "listing header missing");
      int persona = prompt.IndexOf("You are eThang Agent", StringComparison.Ordinal);
      string listing = prompt[listingStart..(persona > listingStart ? persona : prompt.Length)];
      bool underExpectedHeader =
        SegmentAfter(listing, GlobalGroupName).Contains(fileSkillName, StringComparison.Ordinal)
        || SegmentAfter(listing, WorkspaceGroupName).Contains(fileSkillName, StringComparison.Ordinal);
      Assert.True(underExpectedHeader,
          $"'{fileSkillName}' must appear under '{GlobalGroupName}' or '{WorkspaceGroupName}'");
    }
    finally
    {
      ws.Delete(true);
      skills.Delete(true);
    }
  }

  [Fact]
  public async Task SystemPrompt_ManualFileSkill_NeverAppears()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-skilllist-manual-ws");
    DirectoryInfo skills = Directory.CreateTempSubdirectory("ethang-e2e-skilllist-manual");
    try
    {
      const string manualSkillName = "e2e-manual-file-skill";
      string skillFolder = Path.Combine(skills.FullName, manualSkillName);
      _ = Directory.CreateDirectory(skillFolder);
      string[] skillLines =
      [
        "---",
        $"name: {manualSkillName}",
        "description: Manual file skill excluded from the always-on listing.",
        "disable-model-invocation: true",
        "---",
        "",
        $"Body of the {manualSkillName} manual file skill.",
        "",
      ];
      await File.WriteAllLinesAsync(Path.Combine(skillFolder, "SKILL.md"), skillLines,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName).ConfigureAwait(true);
      _ = await host.Store.SetAsync(
        SkillDirectoryPreferences.WorkspaceKey(ws.FullName),
        SkillDirectoryPreferences.Serialize([new SessionFileEntry(skills.FullName, Enabled: true)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

      string prompt = await OpenedSystemPromptAsync(host, ws.FullName).ConfigureAwait(true);

      Assert.DoesNotContain(manualSkillName, prompt, StringComparison.Ordinal);
    }
    finally
    {
      ws.Delete(true);
      skills.Delete(true);
    }
  }

  [Fact]
  public async Task SecondSession_AfterPreferenceChange_ReflectsNewDirectories()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-skilllist-change");
    DirectoryInfo first = Directory.CreateTempSubdirectory("ethang-e2e-skilllist-first");
    DirectoryInfo second = Directory.CreateTempSubdirectory("ethang-e2e-skilllist-second");
    try
    {
      WriteFileSkill(first.FullName, "e2e-first-dir-skill");
      WriteFileSkill(second.FullName, "e2e-second-dir-skill");

      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName).ConfigureAwait(true);
      _ = await host.Store.SetAsync(
        SkillDirectoryPreferences.WorkspaceKey(ws.FullName),
        SkillDirectoryPreferences.Serialize([new SessionFileEntry(first.FullName, Enabled: true)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

      string firstPrompt = await OpenedSystemPromptAsync(host, ws.FullName).ConfigureAwait(true);
      Assert.Contains("e2e-first-dir-skill", firstPrompt, StringComparison.Ordinal);
      Assert.DoesNotContain("e2e-second-dir-skill", firstPrompt, StringComparison.Ordinal);

      // The SECOND session opens only after the preference change: the fresh
      // container resolves the NEW directory list (stale-container failure mode).
      _ = await host.Store.SetAsync(
        SkillDirectoryPreferences.WorkspaceKey(ws.FullName),
        SkillDirectoryPreferences.Serialize([new SessionFileEntry(second.FullName, Enabled: true)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

      string secondPrompt = await OpenedSystemPromptAsync(host, ws.FullName).ConfigureAwait(true);
      Assert.Contains("e2e-second-dir-skill", secondPrompt, StringComparison.Ordinal);
      Assert.DoesNotContain("e2e-first-dir-skill", secondPrompt, StringComparison.Ordinal);
    }
    finally
    {
      ws.Delete(true);
      first.Delete(true);
      second.Delete(true);
    }
  }

  private static void WriteFileSkill(string directory, string name)
  {
    string skillFolder = Path.Combine(directory, name);
    _ = Directory.CreateDirectory(skillFolder);
    string[] skillLines =
    [
      "---",
      $"name: {name}",
      $"description: {name} listed from its configured directory.",
      "---",
      "",
      $"Body of the {name} file skill.",
      "",
    ];
    File.WriteAllLines(Path.Combine(skillFolder, "SKILL.md"), skillLines);
  }

  /// <summary>The listing segment from one group header to the next header or the
  ///     block's end - where a group's entries render.</summary>
  private static string SegmentAfter(string listing, string header)
  {
    int start = listing.IndexOf(header, StringComparison.Ordinal);
    if (start < 0)
    {
      return string.Empty;
    }

    int next = listing.IndexOf('\n', start + header.Length + 1);
    while (next >= 0)
    {
      int lineEnd = listing.IndexOf('\n', next + 1);
      string line = listing[(next + 1)..(lineEnd < 0 ? listing.Length : lineEnd)];
      if (line.StartsWith("[skills listing", StringComparison.Ordinal) || line.StartsWith("[warning]", StringComparison.Ordinal) || line.StartsWith("[collision]", StringComparison.Ordinal))
      {
        break;
      }

      next = lineEnd;
    }

    return next < 0 ? listing[start..] : listing[start..next];
  }
}
