using eThangAgent.ConversationDomain;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition;

// CA1031: the two catch-alls ARE the named best-effort decision (spec #26) - a broken
// announcement channel may never break the reload loop.
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>Hot reload (spec #26): the announcement relay. Renders ONE bounded
///     System line from a <see cref="SkillReloadDiff"/> and delivers it on both
///     channels - the session conversation (the model reads it on the next turn)
///     and the session notice sink (the transcript shows it). Out-of-turn appends
///     follow the accepted UserCommandRunner precedent. Zero changes announce
///     nothing. Every delivery is best-effort: a conversation or sink failure is
///     swallowed - a broken announcement never breaks the watcher loop.</summary>
public sealed class SkillDirectoryReloader(Func<Conversation?> conversation, Action<string>? noticeSink)
{
  private const int MaxEntries = 10;

  public void Announce(SkillReloadDiff diff)
  {
    ArgumentNullException.ThrowIfNull(diff);
    string? line = Render(diff);
    if (line is null)
    {
      return;
    }

    Conversation? target = null;
    try
    {
      target = conversation();
      target?.AddSystemMessage(line);
    }
    catch
    {
      // Best-effort (named decision, spec #26): the announcement may never break
      // the reload loop. The notice sink below still fires.
    }

    try
    {
      noticeSink?.Invoke(line);
    }
    catch
    {
      // Same best-effort contract on the transcript side.
    }
  }

  /// <summary>The verbatim render: header, then up to ten entries -
  ///     "+ name: desc" added, "- name" removed, "~ name: desc" changed -
  ///     joined by "; ", then "; +N more (call skill_list)" when truncated.
  ///     Descriptions truncate at the listing's 60-character budget.</summary>
  public static string? Render(SkillReloadDiff diff)
  {
    ArgumentNullException.ThrowIfNull(diff);
    List<string> entries = [];
    foreach (SkillDefinition skill in diff.Added)
    {
      entries.Add("+ " + skill.Name + ": " + Truncate(skill.Description));
    }

    foreach (SkillDefinition skill in diff.Removed)
    {
      entries.Add("- " + skill.Name);
    }

    foreach (SkillDefinition skill in diff.Changed)
    {
      entries.Add("~ " + skill.Name + ": " + Truncate(skill.Description));
    }

    if (entries.Count == 0)
    {
      return null;
    }

    string tail = entries.Count > MaxEntries
        ? "; +" + (entries.Count - MaxEntries).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more (call skill_list)"
        : string.Empty;
    return "[skills reloaded] " + string.Join("; ", entries.Take(MaxEntries)) + tail;
  }

  private static string Truncate(string description) =>
      description.Length <= 60 ? description : description[..60] + '…';
}
