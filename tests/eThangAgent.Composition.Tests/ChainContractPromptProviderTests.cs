using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The chain contract's prompt provider (issue #311 D1-D3).
///     <para>THE TWO CRITERIA THIS CLASS EXISTS FOR: a session started FOR a chain step gets the
///     contract, and a session started outside a chain gets a BYTE-IDENTICAL prompt to the one
///     it got before this provider existed. The second is the harder one to hold, because it is
///     an absence — so it is pinned against the composite prompt the composition actually
///     builds, not against a hand-assembled list.</para></summary>
public sealed class ChainContractPromptProviderTests
{
  private static ChainContract Contract() =>
      new(7, 104, 2, "sci.dashboard-truth/ledger", ["dotnet build"]);

  [Fact]
  public void Build_ForAChainStep_RendersTheContract()
  {
    string prompt = new ChainContractPromptProvider(Contract()).Build();

    Assert.Contains(ChainContractRenderer.Prefix, prompt, StringComparison.Ordinal);
    Assert.Contains("You are step 2 of chain #7", prompt, StringComparison.Ordinal);
  }

  [Fact]
  public void Build_ForASessionOutsideAChain_RendersEmpty() =>
      // D2: the ordinary way a user begins work is not a chain step. Empty is the whole
      // mechanism — the composite drops empty entries, so the prompt is unchanged.
      Assert.Equal(string.Empty, new ChainContractPromptProvider(null).Build(), StringComparer.Ordinal);

  [Fact]
  public void Build_IsPure_TwoCallsProduceTheSameBytes()
  {
    // The purity contract, pinned the way SessionFilesPromptProvider's construction-time
    // snapshot is: the contract is a value taken at construction, never a read inside Build.
    ChainContractPromptProvider provider = new(Contract());

    Assert.Equal(provider.Build(), provider.Build(), StringComparer.Ordinal);
  }

  [Fact]
  public void Build_DoesNotReReadTheCarrier_SoALaterFillCannotChangeThePrompt()
  {
    // The carrier is construction-phase glue. A provider that consulted it inside Build would
    // change the prompt mid-session and silently invalidate the provider's prompt cache.
    ChainContractCarrier carrier = new();
    carrier.Fill(null);
    ChainContractPromptProvider provider = new(carrier.Contract);
    string before = provider.Build();

    carrier.Fill(Contract());

    Assert.Equal(before, provider.Build(), StringComparer.Ordinal);
    Assert.Equal(string.Empty, before, StringComparer.Ordinal);
  }

  private static ServiceProvider BuildCore(ChainContract? contract)
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    using TestAppDatabase db = TestAppDatabase.Create();
    ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()),
            db.Database)
        .BuildServiceProvider();
    // The carrier is filled AFTER the build, exactly as the factory and the host fill it —
    // the registration is unconditional and resolves null until then.
    services.GetRequiredService<ChainContractCarrier>().Fill(contract);
    return services;
  }

  [Fact]
  public void CompositePrompt_ForAChainStep_CarriesTheContract()
  {
    using ServiceProvider services = BuildCore(Contract());

    string prompt = services.GetRequiredService<ISystemPromptProvider>().Build();

    Assert.Contains(ChainContractRenderer.Prefix, prompt, StringComparison.Ordinal);
    Assert.Contains("sci.dashboard-truth/ledger", prompt, StringComparison.Ordinal);
  }

  [Fact]
  public void CompositePrompt_OutsideAChain_IsByteIdenticalToThePromptWithoutTheProvider()
  {
    // THE CRITERION, stated as a comparison rather than as a contains-check: the composition's
    // prompt for a session with no chain must equal the prompt the same composition builds when
    // the chain provider renders nothing at all. Anything this provider leaked would show up
    // here as a difference in bytes.
    using ServiceProvider services = BuildCore(contract: null);
    string actual = services.GetRequiredService<ISystemPromptProvider>().Build();

    string expected = new CompositeSystemPromptProvider(
    [
        new SkillsBootstrapPromptProvider(services.GetRequiredService<ISkillCatalog>(),
            services.GetRequiredService<ICommitStyleProvider>()),
        new SkillsListingPromptProvider(services.GetRequiredService<ISkillCatalog>(),
            services.GetRequiredService<ILearnedSkillStore>(),
            services.GetRequiredService<ResolvedSkillDirectories>().List),
        new StaticPromptProvider(
            "You are eThang Agent, an AI coding agent for Windows. Work in the current " +
            "workspace, prefer the provided tools over guessing, and keep responses tight."),
        new ExecGuidePromptProvider(
            new Lazy<ICapabilityRegistry>(services.GetRequiredService<ICapabilityRegistry>)),
        new ContextHygienePromptProvider(),
        new TrustedDirectoriesPromptProvider(services.GetRequiredService<ResolvedTrustedDirectories>().List),
    ]).Build();

    Assert.Equal(expected, actual, StringComparer.Ordinal);
  }
}
