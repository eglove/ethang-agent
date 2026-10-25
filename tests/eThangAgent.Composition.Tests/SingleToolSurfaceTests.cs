using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>THE SINGLE-DOOR INVARIANT (issue #288). exec is the only top-level tool, and
///     every other capability is reached as Tools.* INSIDE an exec program. Nothing
///     documented ever decided which side a tool landed on: six tools were reachable both
///     ways and twenty-two only inside exec, and a model that read its tool list literally
///     called 'read' at top level and got Error [UnknownTool] for the most obvious verb in
///     any coding task (production session 3ece7e12, model nvidia/nemotron-3.5-super-vl:free,
///     two wasted iterations rediscovering the surface).
///     <para>These tests pin the invariant from both sides — the loop registry holds
///     exactly one definition, and the capability surface holds everything else — so the
///     surface cannot quietly grow a second door again.</para></summary>
public class SingleToolSurfaceTests
{
  private static ServiceProvider Build(bool computerUse)
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2),
        ComputerUse: computerUse);
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  /// <summary>The invariant, stated as the issue states it: the loop registry's
  ///     Definitions contains exactly one tool, and it is exec.</summary>
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void Loop_Registry_Definitions_Contains_Exactly_One_Tool_And_It_Is_Exec(bool computerUse)
  {
    using ServiceProvider services = Build(computerUse);
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();

    ToolDefinition only = Assert.Single(registry.Definitions);
    Assert.Equal(ExecTool.ToolName, only.Name);
    Assert.NotNull(registry.Find(ExecTool.ToolName));
  }

  /// <summary>Every tool that used to be a second door is still REACHABLE - as a
  ///     capability action exec dispatches against. Removing them from the loop registry
  ///     must never remove them from the harness.</summary>
  [Theory]
  [InlineData("mcp")]
  [InlineData("openrouter_management")]
  [InlineData("read_image")]
  [InlineData("docx")]
  [InlineData("pdf")]
  [InlineData("computer")]
  public void Former_Top_Level_Tools_Resolve_On_The_Capability_Surface(string action)
  {
    // Computer use ON: the gate has its own matrix (ComputerUseCompositionTests); this
    // test is about the door, so the surface is measured with everything enabled.
    using ServiceProvider services = Build(computerUse: true);
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();

    Assert.True(surface().Resolve(action).IsSuccess,
        $"'{action}' left the loop registry but is not reachable inside exec either.");
  }

  /// <summary>And the loop registry cannot grow a second door back: every capability
  ///     action the surface exposes (except exec itself, which is deliberately absent -
  ///     exec cannot call itself) is reached ONLY through the registry.</summary>
  [Fact]
  public void No_Capability_Action_Is_Also_A_Loop_Tool()
  {
    using ServiceProvider services = Build(computerUse: true);
    IToolRegistry loop = services.GetRequiredService<IToolRegistry>();
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();

    HashSet<string> actions = [.. surface().Providers.SelectMany(p => p.Actions).Select(a => a.Name)];
    Assert.True(actions.Count > 20, $"enumeration is vacuous: {actions.Count} action(s) discovered.");

    Assert.DoesNotContain(loop.Definitions, d => actions.Contains(d.Name));
    Assert.DoesNotContain(ExecTool.ToolName, actions);
  }

  /// <summary>The system prompt and the tool list must agree: the guide says exec is the
  ///     only tool, so a model that follows one and reads the other cannot be misled.</summary>
  [Fact]
  public void Exec_Guide_States_The_Invariant()
  {
    using ServiceProvider services = Build(computerUse: true);

    Assert.Contains("exec is the only tool", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("Tools.List()", ExecGuide.Text, StringComparison.Ordinal);
    string prompt = services.GetRequiredService<ISystemPromptProvider>().Build();
    Assert.Contains("exec is the only tool", prompt, StringComparison.Ordinal);
  }
}
