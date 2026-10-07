using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Advertised-contract invariant over the composed ITool surface: every
///     ITool-backed action is TimeoutPolicy.SelfManaged — it parses its own
///     timeoutSeconds envelope (ToolCallEnvelopeParser) — so its contract must
///     DECLARE the timeoutSeconds parameter. ScriptTools.Invoke branches on exactly
///     that declaration: a declaring action gets an inherited budget injected, an
///     undeclaring one gets the key stripped and then fails its own envelope parse
///     with MissingParameter even when the caller stated the budget. The invariant
///     guards the whole surface, including tools added later.</summary>
public class ToolContractConsistencyTests
{
  private static ServiceProvider Build()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  [Fact]
  public void EveryAgentToolAction_DeclaresTimeoutSeconds()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

    List<string> undeclared = [.. tools.Actions
        .Where(a => !a.Parameters.Any(p => p.Name == ToolTimeout.ParameterName))
        .Select(a => a.Name)];

    Assert.True(undeclared.Count == 0,
        "ITool-backed actions must declare the timeoutSeconds parameter " +
        "(ScriptTools injects the inherited budget only for declaring contracts): " +
        string.Join(", ", undeclared));
  }

  /// <summary>End-to-end over the composition: an exec script makes the exact
  ///     nested skill_manage call that failed MissingParameter before the fix —
  ///     the bridge stripped the budget because the contract did not declare the
  ///     parameter. Declaring it makes the injected budget survive the tool's own
  ///     envelope parse, so the nested call dispatches and returns in-band.</summary>
  [Fact]
  public async Task NestedSkillManageCall_ExecRoundTrip_Succeeds()
  {
    using ServiceProvider services = Build();
    IExecEngine engine = services.GetRequiredService<IExecEngine>();

    ExecRunResult run = await engine.ExecuteAsync(
        new ExecProgram(
            "return Tools.Invoke(\"skill_manage\", new { timeoutSeconds = 30, action = \"Lint\", name = \"no-such-skill-anywhere\" });",
            TimeSpan.FromSeconds(120)),
        ct: TestContext.Current.CancellationToken);

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Empty(run.ErrorLines);
    // The tool ran: its own typed not-found error comes back in-band (not the
    // envelope's MissingParameter the old contract produced).
    Assert.Contains("Error [SkillNotFound]", run.Output, StringComparison.Ordinal);
  }
}
