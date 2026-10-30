using System.Text.RegularExpressions;

namespace eThangAgent.SkillDomain;

/// <summary>One skill's reference to a capability action that does not resolve on the live
///     surface, or to a dispatch form the harness does not have.
///     <para><see cref="Reference"/> is the text AS WRITTEN (e.g. <c>agent.spawn</c>), so a
///     failure names what to fix rather than a byte offset.</para></summary>
public sealed record SkillToolReferenceFinding(string Skill, int Line, string Reference, string Reason);

/// <summary>The mechanical guard issue #308 asks for: no embedded skill may name a tool the
///     harness does not have.
///     <para>WHY A MECHANICAL GUARD AND NOT CAREFUL WRITING: a skill is the model's map of the
///     machinery. A stale map is worse than no map, because the model acts on it confidently —
///     it calls the documented action, gets an error, and spends iterations rediscovering a
///     surface that moved. Every previous rewrite of these skills was done by hand and drifted
///     again; a test that reads the LIVE registry cannot drift, because it has no copy of the
///     list to fall out of date.</para>
///     <para>TWO REFERENCE FORMS, because the skills use both. A dotted name
///     (<c>plan.update-step</c>) is an action reference and must resolve. A</para>
///     <c>Tools.&lt;name&gt;</c> form is a dispatch and must be a real action — except the two
///     documented meta-methods <c>Tools.List</c> and <c>Tools.Describe</c>, which are script
///     locals the exec engine exposes to enumerate the surface, not actions on it.
///     <para>IT DELIBERATELY DOES NOT KNOW WHAT A RETIRED ACTION IS. #316 retires the messaging
///     actions; when it does, this guard must fail the skills that still name them — that is the
///     guard working, not a false positive. Writing it against a hard-coded allowlist would make
///     it a second copy of the surface, which is the defect it exists to catch.</para>
///     <para>PURE: text in, findings out. No catalog, no registry, no I/O — so the same corpus
///     always yields the same answer and the composition test can drive it over the real
///     embedded resources against the real registry.</para></summary>
public static partial class SkillToolReferenceScanner
{
  /// <summary>The two exec script locals that ENUMERATE the surface rather than being
  ///     actions on it. Named explicitly because they are the only documented exceptions;
  ///     anything else in <c>Tools.*</c> form must be a real action.</summary>
  public static readonly IReadOnlyList<string> ScriptLocals = ["List", "Describe"];

  /// <summary>A dotted action reference: <c>provider.action</c>, bounded so it does not
  ///     swallow a longer identifier, a file extension, or a URL. Provider and action are
  ///     both lowercase-with-hyphens, which is the capability name rule.</summary>
  [GeneratedRegex("(?<![A-Za-z0-9_.\\-])([a-z][a-z0-9\\-]*)\\.([a-z][a-z0-9\\-]*)(?![A-Za-z0-9_\\-])")]
  private static partial Regex Dotted();

  /// <summary>A <c>Tools.&lt;name&gt;</c> dispatch, in both spellings the skills use: the
  ///     method form <c>Tools.read(</c> and the generic form
  ///     <c>Tools.Invoke("read", ...)</c>.</summary>
  /// <summary>The GENERIC form is matched FIRST on purpose: <c>Tools.Invoke("read", ...)</c>
  ///     also matches the method-form alternative (with <c>Invoke</c> as the name), and a
  ///     scanner that reported <c>Tools.Invoke</c> as a missing action would fail every skill
  ///     that uses the documented generic dispatch. Alternation is ordered, so the specific
  ///     spelling wins.</summary>
  [GeneratedRegex("Tools\\.Invoke\\(\\s*\"([A-Za-z_][A-Za-z0-9_]*)\"|Tools\\.([A-Za-z_][A-Za-z0-9_]*)\\s*\\(")]
  private static partial Regex Dispatch();

  /// <summary>Scans one skill body.</summary>
  /// <param name="skill">The skill name, used only to label findings.</param>
  /// <param name="body">The skill text.</param>
  /// <param name="isKnownProvider">Whether a dotted reference's first segment names a
  ///     capability provider at all. A reference whose provider is unknown is NOT a
  ///     violation: the skills are full of ordinary prose like <c>agentskills.io</c> and
  ///     <c>package.json</c>, and a scanner that flagged those would be turned off within a
  ///     day. Only a reference into a provider we actually have is checked, because that is
  ///     the case where a stale action name is unambiguous.</param>
  /// <param name="hasAction">Whether a dotted reference resolves on that provider.</param>
  /// <param name="hasTopLevelAction">Whether a <c>Tools.*</c> dispatch names a real action.</param>
  public static IReadOnlyList<SkillToolReferenceFinding> Scan(
      string skill, string body,
      Func<string, bool> isKnownProvider,
      Func<string, string, bool> hasAction,
      Func<string, bool> hasTopLevelAction)
  {
    ArgumentNullException.ThrowIfNull(skill);
    ArgumentNullException.ThrowIfNull(body);
    ArgumentNullException.ThrowIfNull(isKnownProvider);
    ArgumentNullException.ThrowIfNull(hasAction);
    ArgumentNullException.ThrowIfNull(hasTopLevelAction);

    List<SkillToolReferenceFinding> findings = [];
    ScanDotted(skill, body, isKnownProvider, hasAction, findings);
    ScanDispatch(skill, body, hasTopLevelAction, findings);
    return findings;
  }

  private static void ScanDotted(string skill, string body, Func<string, bool> isKnownProvider,
      Func<string, string, bool> hasAction, List<SkillToolReferenceFinding> findings)
  {
    foreach (Match match in Dotted().Matches(body))
    {
      string provider = match.Groups[1].Value;
      string action = match.Groups[2].Value;
      if (!isKnownProvider(provider) || hasAction(provider, action))
      {
        continue;
      }

      findings.Add(new SkillToolReferenceFinding(skill, LineOf(body, match.Index),
          $"{provider}.{action}",
          $"'{provider}' has no action '{action}'. Run Tools.List() for the live surface."));
    }
  }

  private static void ScanDispatch(string skill, string body, Func<string, bool> hasTopLevelAction,
      List<SkillToolReferenceFinding> findings)
  {
    foreach (Match match in Dispatch().Matches(body))
    {
      // Group 1 is the quoted name inside Tools.Invoke (matched first); group 2 is the
      // method form. Exactly one of them participates in any given match.
      string name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
      if (ScriptLocals.Contains(name, StringComparer.Ordinal) || hasTopLevelAction(name))
      {
        continue;
      }

      findings.Add(new SkillToolReferenceFinding(skill, LineOf(body, match.Index),
          $"Tools.{name}",
          $"no capability action '{name}' exists. Run Tools.List() for the live surface."));
    }
  }

  /// <summary>The 1-based line a byte offset falls on, so a finding points at a place a
  ///     reader can open.</summary>
  private static int LineOf(string body, int index)
  {
    int line = 1;
    for (int i = 0; i < index && i < body.Length; i++)
    {
      if (body[i] == '\n')
      {
        line++;
      }
    }

    return line;
  }
}
