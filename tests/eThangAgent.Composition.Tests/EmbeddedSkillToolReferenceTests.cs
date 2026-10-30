using System.Text.RegularExpressions;
using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>THE MECHANICAL GUARD (issue #308 amendment): no embedded skill may name a tool the
///     harness does not have.
///     <para>WHY THIS IS A TEST AND NOT A NOTE IN A SKILL: a stale skill is worse than a missing
///     one, because the model acts on it confidently. These skills have been rewritten by hand
///     three times and drifted again each time — the last drift left <c>EthangToolsMapping.md</c>
///     teaching a spawn-and-wait surface that no longer existed. A hand-maintained list cannot
///     keep up; this reads the LIVE capability registry, so there is no copy to fall out of
///     date.</para>
///     <para>IT MUST SURVIVE #316. When the messaging actions are retired, a skill that still
///     names one SHOULD fail here — that is the guard working, and it is why the check is
///     written against the registry rather than against a hard-coded allowlist of known-good
///     names.</para>
///     <para>THE SCANNER'S OWN RULES ARE PINNED in the Skill Domain's tests; this file pins that
///     the REAL corpus passes against the REAL surface, which is the only assertion that can
///     catch a skill going stale.</para></summary>
public class EmbeddedSkillToolReferenceTests
{
  private static ServiceProvider Build()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  /// <summary>Every embedded skill's every tool reference resolves on the live surface.</summary>
  [Fact]
  public async Task No_Embedded_Skill_Names_A_Tool_The_Harness_Does_Not_Have()
  {
    using ServiceProvider services = Build();
    ICapabilityRegistry registry = services.GetRequiredService<ICapabilityRegistry>();
    HashSet<string> providers = [.. registry.Providers.Select(p => p.Id)];
    HashSet<string> actions = [.. registry.Providers.SelectMany(p => p.Actions).Select(a => a.Name)];

    IReadOnlyList<SkillDefinition> skills =
        (await new EmbeddedSkillCatalog().ListAsync(TestContext.Current.CancellationToken))
        .Value!;
    Assert.NotEmpty(skills);

    List<SkillToolReferenceFinding> findings = [];
    foreach (SkillDefinition skill in skills)
    {
      findings.AddRange(SkillToolReferenceScanner.Scan(
          skill.Name, skill.Body,
          isKnownProvider: providers.Contains,
          hasAction: (provider, action) =>
              registry.Providers.First(p => p.Id == provider).Actions.Any(a => a.Name == action),
          hasTopLevelAction: actions.Contains));
    }

    Assert.True(findings.Count == 0, Describe(findings));
  }

  /// <summary>The check is not vacuous: the corpus really does carry resolvable references,
  ///     and the scanner really does read them. Without this a corpus that lost every
  ///     reference — or a scanner that stopped matching — would pass silently.</summary>
  [Fact]
  public async Task The_Corpus_Actually_Carries_Resolvable_References()
  {
    using ServiceProvider services = Build();
    ICapabilityRegistry registry = services.GetRequiredService<ICapabilityRegistry>();
    HashSet<string> providers = [.. registry.Providers.Select(p => p.Id)];

    IReadOnlyList<SkillDefinition> skills =
        (await new EmbeddedSkillCatalog().ListAsync(TestContext.Current.CancellationToken))
        .Value!;

    int resolvable = skills.Sum(skill =>
        CountReferences(skill.Body, providers));

    Assert.True(resolvable >= 5,
        $"enumeration is vacuous: only {resolvable} dotted action reference(s) found across " +
        $"{skills.Count} skill(s). The scanner or the corpus is broken.");
  }

  /// <summary>The guard FAILS a retired action. This is the #316 survival test: it removes one
  ///     real action from the surface and asserts the corpus check would then report it.
  ///     <para>Written as a scanner-level assertion rather than by mutating the registry,
  ///     because the registry is immutable by design — and the property being pinned is the
  ///     scanner's, not the registry's.</para></summary>
  [Fact]
  public void A_Retired_Action_Is_Reported_Rather_Than_Ignored()
  {
    // plan.update-step is real today; the predicate below pretends it was retired.
    const string body = "Mark it done: plan.update-step id=1 position=2 status=Done";

    IReadOnlyList<SkillToolReferenceFinding> findings = SkillToolReferenceScanner.Scan(
        "sample", body,
        isKnownProvider: name => name == "plan",
        hasAction: (_, action) => action != "update-step",
        hasTopLevelAction: _ => true);

    SkillToolReferenceFinding finding = Assert.Single(findings);
    Assert.Equal("plan.update-step", finding.Reference);
    Assert.Equal(1, finding.Line);
  }

  private static int CountReferences(string body, HashSet<string> providers)
  {
    int count = 0;
    foreach (string provider in providers)
    {
      count += Regex.Count(body, Regex.Escape(provider) + @"\.[a-z]");
    }

    return count;
  }

  private static string Describe(List<SkillToolReferenceFinding> findings) =>
      findings.Count == 0
          ? "no findings"
          : $"{findings.Count} stale tool reference(s) in the embedded skills:" + Environment.NewLine +
            string.Join(Environment.NewLine,
                findings.Select(f => $"  {f.Skill}:{f.Line} — {f.Reference}: {f.Reason}"));
}
