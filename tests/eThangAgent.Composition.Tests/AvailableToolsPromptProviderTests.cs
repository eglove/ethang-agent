using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SkillDomain;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The announced-tools prompt entry (plan #209).
///     <para>THE TWO CRITERIA THIS CLASS EXISTS FOR: a user who ticks nothing gets a prompt
///     BYTE-IDENTICAL to the one they got before this provider existed, and every name the
///     prompt announces is one this machine can actually run. The second is why a ticked name
///     that no longer resolves must leave no trace - announcing an unrunnable tool is the
///     exact failure the capability exists to end.</para></summary>
[Trait("Category", "Process")]
public sealed class AvailableToolsPromptProviderTests
{
  [Fact]
  public void Build_WithNothingTicked_RendersEmpty() =>
      // Empty is the whole mechanism: the composite drops empty entries, so a fresh install
      // announces nothing and the prompt is unchanged.
      Assert.Equal(string.Empty, new AvailableToolsPromptProvider([]).Build(), StringComparer.Ordinal);

  [Fact]
  public void Build_WithNoResolvingName_RendersEmpty()
  {
    string directory = NewDirectory();
    try
    {
      // Ticked, but nothing on this PATH runs it: the entry vanishes rather than naming a
      // tool the model would then fail to start.
      ResolvedAvailableTools resolved = ResolvedAvailableTools.Resolve("""["nosuchtool"]""", [directory]);

      Assert.Empty(resolved.List);
      Assert.Equal(string.Empty, new AvailableToolsPromptProvider(resolved.List).Build(), StringComparer.Ordinal);
    }
    finally
    {
      Cleanup(directory);
    }
  }

  [Fact]
  public void Build_NamesOnlyTheToolsThatResolve()
  {
    string directory = NewDirectory();
    try
    {
      WriteScript(directory, "rg");
      ResolvedAvailableTools resolved =
          ResolvedAvailableTools.Resolve("""["rg", "nosuchtool"]""", [directory]);

      string prompt = new AvailableToolsPromptProvider(resolved.List).Build();

      Assert.Contains("rg", prompt, StringComparison.Ordinal);
      // SILENT omission: no error, no placeholder, no mention of the name that failed.
      Assert.DoesNotContain("nosuchtool", prompt, StringComparison.Ordinal);
    }
    finally
    {
      Cleanup(directory);
    }
  }

  [Fact]
  public void Build_KeepsStoredOrderAndSeparatesNamesWithCommas()
  {
    string directory = NewDirectory();
    try
    {
      WriteScript(directory, "rg");
      WriteScript(directory, "jq");
      WriteScript(directory, "fzf");
      ResolvedAvailableTools resolved =
          ResolvedAvailableTools.Resolve("""["rg", "jq", "fzf"]""", [directory]);

      Assert.Contains("rg, jq, fzf", new AvailableToolsPromptProvider(resolved.List).Build(),
          StringComparison.Ordinal);
    }
    finally
    {
      Cleanup(directory);
    }
  }

  [Fact]
  public void Build_AnnouncesAStoredExtensionAsTheBareName()
  {
    string directory = NewDirectory();
    try
    {
      WriteScript(directory, "rg");
      ResolvedAvailableTools resolved = ResolvedAvailableTools.Resolve("""["rg.exe"]""", [directory]);

      string prompt = new AvailableToolsPromptProvider(resolved.List).Build();

      Assert.Contains("rg", prompt, StringComparison.Ordinal);
      Assert.DoesNotContain("rg.exe", prompt, StringComparison.Ordinal);
    }
    finally
    {
      Cleanup(directory);
    }
  }

  [Fact]
  public void Build_CarriesNoVersionOrDescription()
  {
    string directory = NewDirectory();
    try
    {
      WriteScript(directory, "rg");
      ResolvedAvailableTools resolved = ResolvedAvailableTools.Resolve("""["rg"]""", [directory]);

      string prompt = new AvailableToolsPromptProvider(resolved.List).Build();

      // No version probing: one process spawn per tool per session start, for text a model
      // does not need. A digit anywhere would mean something enriched the announcement.
      Assert.DoesNotContain(prompt, char.IsAsciiDigit);
    }
    finally
    {
      Cleanup(directory);
    }
  }

  [Fact]
  public void Build_StatesTheToolsAreOnPathAndThatOthersMayExist()
  {
    string prompt = new AvailableToolsPromptProvider(["rg"]).Build();

    Assert.Contains("PATH", prompt, StringComparison.Ordinal);
    Assert.Contains("run directly", prompt, StringComparison.Ordinal);
    // The list is never exhaustive, and a model that read it as such would stop looking.
    Assert.Contains("not listed", prompt, StringComparison.Ordinal);
  }

  [Fact]
  public void Build_IsPure_TwoCallsProduceTheSameBytes()
  {
    AvailableToolsPromptProvider provider = new(["rg", "jq"]);

    Assert.Equal(provider.Build(), provider.Build(), StringComparer.Ordinal);
  }

  [Fact]
  public void Resolve_ReusesTheStoredValueOnTheResult()
  {
    // The raw text rides the result so the host re-resolves through the same rule instead of
    // trusting a list another process computed.
    ResolvedAvailableTools resolved = ResolvedAvailableTools.Resolve("""["rg"]""", []);

    Assert.Equal("""["rg"]""", resolved.Stored, StringComparer.Ordinal);
  }

  [Fact]
  public void Resolve_WithAMalformedStoredValue_AnnouncesNothing()
  {
    // A bad stored value never half-announces a partially understood set.
    ResolvedAvailableTools resolved = ResolvedAvailableTools.Resolve("not json", []);

    Assert.Empty(resolved.List);
    Assert.Equal(string.Empty, new AvailableToolsPromptProvider(resolved.List).Build(), StringComparer.Ordinal);
  }

  [Fact]
  public void CompositePrompt_WithNothingTicked_IsByteIdenticalToThePromptWithoutTheProvider()
  {
    // THE CRITERION, stated as a comparison rather than a contains-check: the composition's
    // prompt for a session with no stored preference must equal the prompt the same
    // composition builds when this provider renders nothing at all.
    using ServiceProvider services = BuildCore(pathTools: null);
    string actual = services.GetRequiredService<ISystemPromptProvider>().Build();

    string expected = new CompositeSystemPromptProvider(
    [
        new SkillsBootstrapPromptProvider(services.GetRequiredService<ISkillCatalog>(),
            services.GetRequiredService<ICommitStyleProvider>()),
        new SkillsListingPromptProvider(services.GetRequiredService<ISkillCatalog>(),
            services.GetRequiredService<ResolvedSkillDirectories>().List),
        new StaticPromptProvider(
            "You are eThang Agent, an AI coding agent for Windows. Work in the current " +
            "workspace, prefer the provided tools over guessing, and keep responses tight."),
        new StakeholderVoicePromptProvider(),
        new ExecGuidePromptProvider(
            new Lazy<ICapabilityRegistry>(services.GetRequiredService<ICapabilityRegistry>)),
        new ContextHygienePromptProvider(),
        new ComputedValuesPromptProvider(),
        new HardeningPromptProvider(),
        new TrustedDirectoriesPromptProvider(services.GetRequiredService<ResolvedTrustedDirectories>().List),
    ]).Build();

    Assert.Equal(expected, actual, StringComparer.Ordinal);
  }

  [Fact]
  public void CompositePrompt_WithATickedTool_NamesIt()
  {
    string directory = NewDirectory();
    try
    {
      WriteScript(directory, "rg");
      using ServiceProvider services = BuildCore(
          PathToolPreferences.Serialize(["rg"]), [directory]);

      string prompt = services.GetRequiredService<ISystemPromptProvider>().Build();

      Assert.Contains("rg", prompt, StringComparison.Ordinal);
    }
    finally
    {
      Cleanup(directory);
    }
  }

  /// <summary>Builds the real core over the stored ticked-tool list. The container's own
  ///     registration resolves against the machine's PATH, so a test that needs a name to
  ///     resolve replaces that registration with one resolved against its own directory -
  ///     the same seam and one indirection less than writing to a preference store.</summary>
  private static ServiceProvider BuildCore(string? pathTools, IReadOnlyList<string>? directories = null)
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new AgentRunOptions(null, 2));
    using TestAppDatabase db = TestAppDatabase.Create();
    IServiceCollection services = new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()),
            db.Database);
    if (directories is not null)
    {
      _ = services.AddSingleton(ResolvedAvailableTools.Resolve(pathTools, directories));
    }

    return services.BuildServiceProvider();
  }

  /// <summary>A temp directory holding one <c>.cmd</c> script, which a PATH lookup resolves
  ///     without reading a PE header and without spawning anything.</summary>
  private static void WriteScript(string directory, string name) =>
      File.WriteAllText(Path.Combine(directory, name + ".cmd"), "@echo off");

  private static string NewDirectory()
  {
    string directory = Path.Combine(TestTempRoot.Root, "ethang-tools-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(directory);
    return directory;
  }

  /// <summary>Best-effort removal of the temp tree this test created.</summary>
  private static void Cleanup(string directory)
  {
#pragma warning disable CA1031 // Best-effort cleanup of temp directories.
#pragma warning disable S108 // Empty catch block is the best-effort cleanup.
    try
    {
      Directory.Delete(directory, recursive: true);
    }
    catch
    {
    }
#pragma warning restore S108
#pragma warning restore CA1031
  }
}
