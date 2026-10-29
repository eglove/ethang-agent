using System.Reflection;
using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Roslyn.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The exec bridge's tool surface is split: identifier-safe actions are
///     direct-bound convenience methods on <see cref="ScriptTools"/> (Tools.read(...)),
///     while everything else is reachable only through Tools.Invoke("name", ...). The
///     split is invisible to the model, and the failure shape is the worst one - a
///     COMPILE error (ExecParseError) that discards the whole script, not an in-band
///     error string a script can branch on (issue #118).
///     <para>This test is the real guard, and it lives HERE rather than beside
///     ScriptTools because the invariant is over the COMPOSED surface: the action names
///     the model actually sees, which only a built container can enumerate. The
///     previous guard read an optional <c>public static ToolName</c> field, which only
///     ExecTool declares - so it enumerated exactly one action and passed vacuously
///     while find_files and mcp had no wrapper at all. Every assertion below therefore
///     also pins that the enumeration itself is non-trivial: a guard that silently
///     enumerates nothing is the bug, not the safety net.</para></summary>
public class ScriptToolsWrapperCoverageTests
{
  // ScriptTools members that are not tool wrappers: the generic dispatch form, the
  // catalog readers, and the static Require helper.
  private static readonly HashSet<string> NonWrapperMethods =
      new(StringComparer.Ordinal) { "Invoke", "List", "Describe" };

  // 'exec' is the one wrapper with no capability action behind it, by design: exec
  // CANNOT call itself (the bootstrap states it outright), so ExecTool is registered on
  // the loop's IToolRegistry as a first-class model-facing tool and never joins the
  // capability surface. Tools.exec(...) therefore exists for surface symmetry and
  // resolves to UnknownAction - a NAMED exception, verified by the Resolve assertion
  // below rather than assumed, so the day exec joins the surface this test says so.
  private static readonly HashSet<string> WrappersWithoutAnAction =
      new(StringComparer.Ordinal) { "exec" };

  private static ServiceProvider Build()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    // ComputerUse is ON so the settings-gated 'computer' tool is part of the surface
    // under test: a gated-off tool would silently exempt its wrapper from the guard.
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2),
        ComputerUse: true);
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  /// <summary>The ITool-backed action names the model can reach, from the real surface.</summary>
  private static IReadOnlyList<string> ActionNames(ServiceProvider services) =>
      [.. services.GetRequiredService<AgentToolsProvider>().Actions.Select(a => a.Name)];

  /// <summary>The convenience methods declared on ScriptTools.</summary>
  private static HashSet<string> WrapperNames() =>
      [.. typeof(ScriptTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
          .Where(m => m.DeclaringType == typeof(ScriptTools))
          .Select(m => m.Name)
          .Where(n => !NonWrapperMethods.Contains(n))];

  /// <summary>True when the name binds as a bare C# identifier (state.set does not).</summary>
  private static bool IsIdentifierSafe(string name) =>
      name.Length > 0 && char.IsAsciiLetterLower(name[0]) &&
      name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

  [Fact]
  public void EveryIdentifierSafeAction_HasAConvenienceMethod()
  {
    using ServiceProvider services = Build();
    HashSet<string> wrappers = WrapperNames();
    List<string> actions = [.. ActionNames(services)];

    // Anti-vacuity: the guard is worthless if it enumerates nothing (the exact defect
    // this test replaces). The surface is ~30 actions; 20 is a floor, not a target.
    Assert.True(actions.Count > 20,
        $"enumeration is vacuous: only {actions.Count} action(s) discovered, so this guard proves nothing.");

    List<string> missing = [.. actions
        .Where(IsIdentifierSafe)
        .Where(n => !wrappers.Contains(n))];

    Assert.True(missing.Count == 0,
        "Identifier-safe tool actions with no ScriptTools convenience method "
        + "(Tools.<name>(...) would be a COMPILE error, losing the whole script): "
        + string.Join(", ", missing));
  }

  [Fact]
  public void EveryConvenienceMethod_NamesARealAction()
  {
    using ServiceProvider services = Build();
    HashSet<string> actions = [.. ActionNames(services)];

    Assert.True(actions.Count > 20,
        $"enumeration is vacuous: only {actions.Count} action(s) discovered.");

    // The inverse direction: a wrapper for an action that no longer exists is a stale
    // surface the model can still compile against, and it dispatches to UnknownAction.
    List<string> stale = [.. WrapperNames()
        .Where(n => !actions.Contains(n))
        .Where(n => !WrappersWithoutAnAction.Contains(n))];

    Assert.True(stale.Count == 0,
        "ScriptTools convenience methods naming no ITool-backed action: "
        + string.Join(", ", stale));

    // The named exception is pinned from both sides: 'exec' must stay OFF the capability
    // surface (a script calling Tools.exec would recurse into the engine). If exec ever
    // joins the surface, this fails and the exclusion above must be deleted.
    Result<ResolvedCapability> execAction =
        services.GetRequiredService<Func<ICapabilityRegistry>>()().Resolve("exec");
    Assert.False(execAction.IsSuccess,
        "'exec' is now a capability action; remove it from WrappersWithoutAnAction.");
  }

  [Fact]
  public void NonIdentifierActionNames_AreNotBoundAsWrappers()
  {
    using ServiceProvider services = Build();
    HashSet<string> wrappers = WrapperNames();

    // Dotted/hyphenated actions (state.set, agent.spawn, plan.create, memory.recall)
    // cannot bind as C# identifiers; a wrapper claiming one would shadow the Invoke
    // form with a name the model cannot actually call.
    List<string> shadowed = [.. ActionNames(services)
        .Where(n => !IsIdentifierSafe(n))
        .Where(wrappers.Contains)];

    Assert.True(shadowed.Count == 0,
        "Non-identifier action names must stay Invoke-only: "
        + string.Join(", ", shadowed));
  }
}
