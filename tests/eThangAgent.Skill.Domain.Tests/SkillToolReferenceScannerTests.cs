using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

/// <summary>The tool-reference scanner behind issue #308's mechanical guard. Its RULES are
///     pinned here; the corpus assertion that uses it lives in Composition.Tests, where the
///     real registry is reachable (this project references only the Skill Domain).
///     <para>Every case below is a false positive the guard would otherwise produce, because a
///     guard that cries wolf gets deleted and then nothing is checked at all.</para></summary>
public class SkillToolReferenceScannerTests
{
  private static readonly HashSet<string> Providers = ["agent", "plan", "state", "chain"];

  private static readonly Dictionary<string, string[]> Actions = new(StringComparer.Ordinal)
  {
    ["agent"] = ["status", "result", "cancel"],
    ["plan"] = ["create", "show", "update-step"],
    ["state"] = ["get", "set", "find"],
    ["chain"] = ["start"],
  };

  private static readonly HashSet<string> TopLevel =
      ["read", "write", "edit", "exec", "skill_view", "git_commit"];

  private static IReadOnlyList<SkillToolReferenceFinding> Scan(string body) =>
      SkillToolReferenceScanner.Scan("sample", body,
          isKnownProvider: Providers.Contains,
          hasAction: (provider, action) => Actions[provider].Contains(action, StringComparer.Ordinal),
          hasTopLevelAction: TopLevel.Contains);

  [Fact]
  public void Scan_ResolvableReferences_YieldNoFindings()
  {
    const string body = "Use plan.update-step, then state.checkgoal is NOT checked here. " +
        "Read with Tools.read( and dispatch with Tools.Invoke(\"skill_view\", args).";

    // state.checkgoal is absent from this fixture's surface, so that one IS a finding.
    IReadOnlyList<SkillToolReferenceFinding> findings = Scan(body);

    SkillToolReferenceFinding finding = Assert.Single(findings);
    Assert.Equal("state.checkgoal", finding.Reference);
  }

  [Fact]
  public void Scan_UnknownProvider_IsNotAViolation()
  {
    // Prose in the corpus is full of dotted names that are not tools: file names, hostnames,
    // and library conventions. Flagging those would make the guard unusable.
    const string body = "See agentskills.io, package.json, pyproject.toml, and example-py.py.";

    Assert.Empty(Scan(body));
  }

  [Fact]
  public void Scan_RetiredAction_NamesTheActionAndItsLine()
  {
    // plan.review is a real action that this fixture's surface deliberately omits, which is
    // what a retirement looks like from the scanner's side.
    const string body = "line one\nline two\nRecord the verdict with plan.review now";

    SkillToolReferenceFinding finding = Assert.Single(Scan(body));
    Assert.Equal("plan.review", finding.Reference);
    Assert.Equal(3, finding.Line);
    Assert.Equal("sample", finding.Skill);
    Assert.Contains("no action 'review'", finding.Reason, StringComparison.Ordinal);
  }

  [Fact]
  public void Scan_ToolsInvoke_ChecksTheQuotedName_NotInvokeItself()
  {
    // The generic dispatch is the documented spelling; reporting 'Tools.Invoke' as a missing
    // action would fail every skill that uses it.
    Assert.Empty(Scan("Tools.Invoke(\"skill_view\", new { name = \"x\" })"));

    SkillToolReferenceFinding finding = Assert.Single(Scan("Tools.Invoke(\"spawn\", new { })"));
    Assert.Equal("Tools.spawn", finding.Reference);
  }

  [Fact]
  public void Scan_ToolsMethodForm_ChecksTheName()
  {
    Assert.Empty(Scan("Tools.read(new { path = \"a.txt\" })"));

    SkillToolReferenceFinding finding = Assert.Single(Scan("Tools.spawn(new { })"));
    Assert.Equal("Tools.spawn", finding.Reference);
  }

  [Fact]
  public void Scan_TheTwoScriptLocals_AreNeverFindings()
  {
    // Tools.List and Tools.Describe enumerate the surface; they are script locals the exec
    // engine exposes, not capability actions, so neither is in TopLevel above.
    Assert.Empty(Scan("Call Tools.List() to see the surface, then Tools.Describe(\"read\")."));
    Assert.Empty(Scan("Call Tools.Invoke(\"List\", null) or Tools.Invoke(\"Describe\", null)."));
  }

  [Fact]
  public void Scan_EmptyBody_YieldsNoFindings() => Assert.Empty(Scan(string.Empty));

  [Fact]
  public void Scan_DottedNameInsideALongerIdentifier_IsNotAReference() =>
      // Bounded on both sides, so a longer identifier that CONTAINS a provider.action pair is
      // not read as a reference: 'mystate' is not the state provider, and 'get_more' is not
      // the 'get' action.
      Assert.Empty(Scan("mystate.get is not state.get, and state.get_more is not state.get."));
}
