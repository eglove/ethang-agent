using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Two status/selection wiring contracts:
///     1. A Model Settings save mid-session must refresh the container's
///     SmartSelectionCarrier - the carrier is filled only at session open, so a
///     save between open and the first turn was invisible to selection (the
///     2026-10-11 incident: options saved after open never reached the resolver).
///     2. A session whose record carries a real model pin shows that model in the
///     status bar at attach - the selection notice fires only on the turn that
///     CHANGES the pin, so a resumed session would otherwise display the bootstrap
///     fallback forever.</summary>
public class StatusModelAndCarrierTests
{
  private const string Provider = "openrouter";
  private const string Root = @"C:\work\status-model-roundtrip";

  [Fact]
  public async Task SettingsSave_RefreshesSmartSelectionCarrier()
  {
    ServiceProvider services = new ServiceCollection()
        .AddSingleton<SmartSelectionCarrier>()
        .BuildServiceProvider();
    SmartSelectionCarrier carrier = services.GetRequiredService<SmartSelectionCarrier>();
    MainViewModel shell = CreateShell(store: null, services);
    _ = await OpenAsync(shell).ConfigureAwait(true);

    SmartSelectionOptions saved = new(0.1m, 2m, null, null, null, null, SmartSortCriterion.Coding);
    await shell.ApplySamplingSettingsAsync(new ModelSettingsSnapshot(
        new SessionModelPreferences(), KnobTexts: new Dictionary<string, string>(),
        ProviderSettingsJson: null, SmartSelectionJson: saved.ToJson())).ConfigureAwait(true);

    Assert.NotNull(carrier.Current);
    Assert.Equal(saved, carrier.Current);
  }

  [Fact]
  public async Task SettingsSave_AllDefault_ClearsCarrier()
  {
    ServiceProvider services = new ServiceCollection()
        .AddSingleton<SmartSelectionCarrier>()
        .BuildServiceProvider();
    SmartSelectionCarrier carrier = services.GetRequiredService<SmartSelectionCarrier>();
    carrier.Fill(new SmartSelectionOptions(0.1m, 2m, null, null, null, null, SmartSortCriterion.Coding));
    MainViewModel shell = CreateShell(store: null, services);
    _ = await OpenAsync(shell).ConfigureAwait(true);

    await shell.ApplySamplingSettingsAsync(new ModelSettingsSnapshot(
        new SessionModelPreferences(), KnobTexts: new Dictionary<string, string>(),
        ProviderSettingsJson: null, SmartSelectionJson: null)).ConfigureAwait(true);

    Assert.Null(carrier.Current);
  }

  [Fact]
  public async Task Attach_RestoresPinnedModelToStatus()
  {
    MainViewModel shell = CreateShell(store: new PinnedStore("z-ai/glm-5.3"), null);
    AgentTabViewModel tab = await OpenAsync(shell).ConfigureAwait(true);

    Assert.Equal("z-ai/glm-5.3", tab.ViewModel.Status.ModelId);
  }

  [Fact]
  public async Task Attach_UnassignedOrFallbackPin_KeepsBootstrapId()
  {
    MainViewModel shell = CreateShell(store: new PinnedStore(AgentRecord.UnassignedModel), null);
    AgentTabViewModel tab = await OpenAsync(shell).ConfigureAwait(true);

    Assert.Equal("test/model", tab.ViewModel.Status.ModelId); // the bootstrap id, untouched
  }

  private static MainViewModel CreateShell(IAgentStore? store, ServiceProvider? services)
  {
    return new MainViewModel(
        (root, provider) => Task.FromResult(Result.Success(BuildSession(root, provider, store, services))),
        new MainViewModelOptions());
  }

  private static async Task<AgentTabViewModel> OpenAsync(MainViewModel shell)
  {
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(Root, Provider).ConfigureAwait(true);
    Assert.True(opened.IsSuccess, $"session open failed: [{opened.Error?.Code}] {opened.Error?.Message}");
    return opened.Value;
  }

  private static AgentSession BuildSession(string root, string provider, IAgentStore? store, ServiceProvider? services)
  {
    // A session whose container these tests never dispose; when a store or a
    // carrier-bearing service collection is supplied it rides the container.
    ServiceCollection descriptions = new();
    if (store is not null)
    {
      _ = descriptions.AddSingleton(store);
    }

    if (services is not null)
    {
      _ = descriptions.AddSingleton(services.GetRequiredService<SmartSelectionCarrier>());
    }

    ServiceProvider container = descriptions.BuildServiceProvider();
    return new AgentSession(
        container,
        AgentId.NewId(),
        new Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", null, 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime(),
        Preferences: new SessionModelPreferences());
  }

  private sealed class PinnedStore(string modelUsed) : IAgentStore
  {
    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(AgentRecord.Root(id, DateTimeOffset.UtcNow, Root, Provider, modelUsed)));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }
}
