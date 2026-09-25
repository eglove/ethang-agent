using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp-file cleanup in finally blocks is deliberate (CA1031).
#pragma warning disable CA1031, S108 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>Skill invocation wiring (plan #30 task 3): the shared invocation
///     core resolves per container over the composite catalog; the
///     skill_invoke tool resolves through its port and sink seams and is
///     advertised on the tool surface (children inherit through the same
///     registration); the AgentSession surface carries the service beside
///     CommandRunner on create AND resume.</summary>
[Collection("EnvironmentSensitive")]
public class SkillInvocationCompositionTests
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new ZaiSettings(null, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2));

  private static ServiceProvider BuildContainer()
  {
    return new ServiceCollection()
        .AddEThangAgentCore(Settings(), Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()))
        .BuildServiceProvider();
  }

  private static (AgentSessionFactory Factory, string DbPath, string Workspace) CreateFactory()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-invoke-{Guid.NewGuid():N}.db");
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    string workspace = Directory.CreateTempSubdirectory("ethang-invoke-ws").FullName;
    return (new AgentSessionFactory(Settings()), dbPath, workspace);
  }

  [Fact]
  public void Service_Resolves_OverCompositeCatalog()
  {
    using ServiceProvider services = BuildContainer();
    SkillInvocationService service = services.GetRequiredService<SkillInvocationService>();
    Assert.NotNull(service);
    ISkillCatalog catalog = services.GetRequiredService<ISkillCatalog>();
    _ = Assert.IsType<CompositeSkillCatalog>(catalog);
  }

  [Fact]
  public void Port_And_Sink_And_Tool_Resolve()
  {
    using ServiceProvider services = BuildContainer();
    _ = Assert.IsType<SkillInvocationPortAdapter>(services.GetRequiredService<ISkillInvocationPort>());
    _ = Assert.IsType<ConversationSinkAdapter>(services.GetRequiredService<IConversationSink>());
    SkillInvokeTool tool = services.GetRequiredService<SkillInvokeTool>();
    Assert.Equal("skill_invoke", tool.Definition.Name);
  }

  [Fact]
  public void Tool_Is_Advertised_On_ToolSurface()
  {
    using ServiceProvider services = BuildContainer();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    Assert.Contains(tools.Actions, a => a.Name == "skill_invoke");
  }

  [Fact]
  public async Task Session_Carries_Invocation_OnCreate_And_Resume()
  {
    (AgentSessionFactory factory, string db, string workspace) = CreateFactory();
    try
    {
      Result<AgentSession> created = await factory.CreateAsync(
          workspace, Providers.OpenRouter, ct: TestContext.Current.CancellationToken);
      Assert.True(created.IsSuccess);
      Assert.NotNull(created.Value.SkillInvocation);

      Result<AgentSession> resumed = await factory.ResumeAsync(
          created.Value.RootId, ct: TestContext.Current.CancellationToken);
      Assert.True(resumed.IsSuccess);
      Assert.NotNull(resumed.Value.SkillInvocation);
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      try
      {
        File.Delete(db);
        Directory.Delete(workspace, true);
      }
      catch { }
    }
  }
}
