using System.Text.RegularExpressions;
using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Every action reference a description makes must resolve on the LIVE surface.
///     <para>WHY THIS IS A GATE AND NOT CAREFUL WRITING: a description is the model's map of
///     the machinery, so a stale one is worse than none - the model calls the action it was
///     told about, gets an error, and spends iterations rediscovering a surface that moved.
///     Hand-written assertions cannot hold this line: each retired action needed its own
///     literal, and the literals are exactly what goes stale. This test keeps no copy of the
///     surface, so it has nothing to fall out of date with.</para>
///     <para>IT READS THE COMPOSED REGISTRY, so an action retired anywhere in the harness is
///     caught in every description that still names it - including descriptions written after
///     this test.</para></summary>
[Trait("Category", "Process")]
public class ActionDescriptionReferenceTests
{
  [Fact]
  public void EveryReferenceInAnActionDescription_ResolvesOnTheLiveSurface()
  {
    (List<string> findings, int examined) = ScanLiveDescriptions();

    Assert.True(findings.Count == 0,
        "a model-facing description names an action that does not resolve:" + Environment.NewLine
        + string.Join(Environment.NewLine, findings));

    // NON-VACUITY: a scan that examined no reference passes no matter what the descriptions
    // say, which is how a guard over an empty corpus fails silently. The surface documents
    // itself, so the scan must have seen references.
    Assert.True(examined > 0, "the scan examined no action reference - the guard is inspecting nothing.");
  }

  /// <summary>The control: the same scan, over text naming a RETIRED action, must report it.
  ///     Without this the test above is satisfied by a scanner that never matches, so a green
  ///     run would prove nothing about stale references.</summary>
  [Fact]
  public void Scan_ReportsATextThatNamesARetiredAction()
  {
    Surface surface = Surface.Load();

    IReadOnlyList<SkillToolReferenceFinding> findings = SkillToolReferenceScanner.Scan(
        "probe", "steer the child with agent.send, then read agent.status.",
        surface.KnowsProvider, surface.HasAction, surface.HasTopLevelAction);

    SkillToolReferenceFinding finding = Assert.Single(findings);
    Assert.Equal("agent.send", finding.Reference);
  }

  /// <summary>Scans every live action's model-facing text, returning the findings and how
  ///     many references the scan actually examined.</summary>
  private static (List<string> Findings, int Examined) ScanLiveDescriptions()
  {
    Surface surface = Surface.Load();

    List<string> findings = [];
    int examined = 0;
    foreach (ProviderCapabilities provider in surface.Registry.Providers)
    {
      foreach (ActionDescriptor action in provider.Actions)
      {
        string text = action.Summary + "\n" + action.Description;
        examined += surface.ReferencedActions(text);
        findings.AddRange(SkillToolReferenceScanner
            .Scan(provider.Id + "." + action.Name, text,
                surface.KnowsProvider, surface.HasAction, surface.HasTopLevelAction)
            .Select(f => f.Skill + ": " + f.Reference + " - " + f.Reason));
      }
    }

    return (findings, examined);
  }

  /// <summary>The composed surface, indexed for the scanner's three predicates.</summary>
  private sealed class Surface
  {
    private readonly Dictionary<string, HashSet<string>> _actions;
    private readonly HashSet<string> _topLevel;

    private Surface(ICapabilityRegistry registry)
    {
      Registry = registry;
      _actions = registry.Providers.ToDictionary(
          p => p.Id,
          p => p.Actions.Select(a => a.Name).ToHashSet(StringComparer.Ordinal),
          StringComparer.Ordinal);
      _topLevel = [.. registry.Providers.SelectMany(p => p.Actions).Select(a => a.Name)];
    }

    public ICapabilityRegistry Registry { get; }

    public static Surface Load()
    {
      using TestAppDatabase db = TestAppDatabase.Create();
      AgentSettings settings = new(
          new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
          new AgentRunOptions(null, 2));
      using ServiceProvider services = new ServiceCollection()
          .AddEThangAgentCore(settings, Providers.OpenRouter,
              ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
              new AgentHostOptions(
                  FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
          .BuildServiceProvider();
      return new Surface(services.GetRequiredService<Func<ICapabilityRegistry>>()());
    }

    public bool KnowsProvider(string provider) => _actions.ContainsKey(provider);

    public bool HasAction(string provider, string action)
        => _actions.TryGetValue(provider, out HashSet<string>? known) && known.Contains(action);

    public bool HasTopLevelAction(string action) => _topLevel.Contains(action);

    /// <summary>How many dotted references in the text point into a provider the harness
    ///     has: the population this guard is responsible for.</summary>
    public int ReferencedActions(string text)
    {
      int count = 0;
      foreach (string provider in _actions.Keys)
      {
        count += Regex.Count(text, Regex.Escape(provider) + @"\.[a-z][a-z0-9\-]*");
      }

      return count;
    }
  }
}
