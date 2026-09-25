using eThangAgent.ConversationDomain;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>The hot-reload coordinator (spec #26): renders the verbatim
/// [skills reloaded] announcement from a SkillReloadDiff, appends it to the
/// conversation and posts it to the notice sink, and no-ops on an empty diff.
/// Announcement shape: header + "+ name: desc" / "- name" / "~ name: desc"
/// entries joined by "; ", bounded to 10 entries with a "+N more" tail.</summary>
public class SkillDirectoryReloaderTests
{
  [Fact]
  public void Apply_EmptyDiff_NoMessageNoCalls()
  {
    Conversation conversation = new();
    int notices = 0;
    SkillDirectoryReloader reloader = new(() => conversation, _ => notices++);

    reloader.Announce(new SkillReloadDiff([], [], []));

    Assert.Empty(conversation.Messages);
    Assert.Equal(0, notices);
  }

  [Fact]
  public void Apply_AddedAndRemoved_RendersEveryKindVerbatim()
  {
    Conversation conversation = new();
    string? notice = null;
    SkillDirectoryReloader reloader = new(() => conversation, m => notice = m);
    SkillDefinition added = Mk("new-skill");
    SkillDefinition removed = Mk("old-skill");
    SkillDefinition changed = Mk("mod-skill");

    reloader.Announce(new SkillReloadDiff([added], [removed], [changed]));

    Message line_message = Assert.Single(conversation.Messages);
    string line = line_message.Content;
    Assert.StartsWith("[skills reloaded] + new-skill: desc new-skill; - old-skill; ~ mod-skill: desc mod-skill", line, StringComparison.Ordinal);
    Assert.Equal(line, notice);
  }

  [Fact]
  public void Apply_MoreThanTenEntries_BoundedWithMoreTail()
  {
    Conversation conversation = new();
    SkillDirectoryReloader reloader = new(() => conversation, _ => { });
    List<SkillDefinition> added = [];
    for (int i = 0; 12 > i; i++)
    {
      added.Add(Mk("s" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    reloader.Announce(new SkillReloadDiff(added, [], []));

    Message line_message = Assert.Single(conversation.Messages);
    string line = line_message.Content;
    Assert.Contains("+s9", line.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal); // 10th entry (s0..s9) present
    Assert.Contains("+2 more (call skill_list)", line, StringComparison.Ordinal);
    Assert.DoesNotContain("s11", line, StringComparison.Ordinal);
  }

  [Fact]
  public void Apply_LongDescriptions_TruncatedAtSixtyChars()
  {
    Conversation conversation = new();
    SkillDirectoryReloader reloader = new(() => conversation, _ => { });
    SkillDefinition longDesc = Mk("long");
    longDesc = longDesc with { Description = new string('x', 80) };

    reloader.Announce(new SkillReloadDiff([longDesc], [], []));

    Message line_message = Assert.Single(conversation.Messages);
    string line = line_message.Content;
    Assert.Contains(new string('x', 60) + "…", line, StringComparison.Ordinal);
    Assert.DoesNotContain(new string('x', 61), line, StringComparison.Ordinal);
  }

  [Fact]
  public void Apply_NullConversation_StillPostsTheNotice()
  {
    string? notice = null;
    SkillDirectoryReloader reloader = new(() => null, m => notice = m);

    reloader.Announce(new SkillReloadDiff([], [], [Mk("c")]));

    Assert.NotNull(notice);
    Assert.StartsWith("[skills reloaded] ~ c:", notice, StringComparison.Ordinal);
  }

  private static SkillDefinition Mk(string name) => new(
      name, "desc " + name, "body " + name, Version: 1, SkillSource.File,
      ProvenanceSessionId: null, CreatedAt: DateTimeOffset.UnixEpoch,
      UpdatedAt: DateTimeOffset.UnixEpoch, Manual: false, Origin: null);
}
