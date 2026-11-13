using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.Transport.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>What the status bar names once an ordered model list is in play: the model that
///     ACTUALLY answered, and how many backups the selection carries.
///     <para>THE REQUESTED HEAD IS NOT THE ANSWER. An ordered list lets the provider serve a
///     backup, and the only place the app learns which entry ran is the provider's own report
///     on the recorded call. A bar that derived the name from the head would claim a model the
///     turn did not run on - the same class of defect the pinned-model restore exists to
///     prevent.</para>
///     <para>AND THE SELECTION IS UNCHANGED BY A FAILOVER: the next turn starts at the head
///     again, so the answering backup is never promoted into the user's list.</para></summary>
public class StatusModelTests
{
  private const string Provider = "openrouter";
  private const string Root = @"C:\work\status-model-list";

  private static string ModelChoiceKey =>
      $"model_provider_choice:{Provider}:{Path.GetFullPath(Root)}";

  // ── 7: the bar names the answering model ──────────────────────────────────

  [Fact]
  public async Task ATurnTheBackupAnswered_NamesTheBackupOnTheBar()
  {
    // A host-run turn reports the served model on its usage row. The bar follows that report,
    // not the head it requested.
    SessionModelPreferences live = new() { ModelIds = ["alpha/model", "beta/other"] };
    FakeHostTurn turn = await FakeHostTurn.StartAsync(live).ConfigureAwait(true);
    turn.Host.Script.Add(new RootTurnEvent("t", "usage",
        RootTurnUsagePayload.Serialize(UsageRow("alpha/model", "beta/other")), 0));

    await turn.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal("beta/other", turn.ViewModel.Status.ModelId);
    // The preference is UNTOUCHED: the tail is still the tail, so the next turn starts at the head.
    Assert.Equal(["alpha/model", "beta/other"], live.ModelIds);
    Assert.Equal("· 1 backup", turn.ViewModel.Status.BackupCountText);
  }

  [Fact]
  public async Task ATurnTheHeadAnswered_LeavesTheBarNamingTheHead()
  {
    SessionModelPreferences live = new() { ModelIds = ["alpha/model", "beta/other"] };
    FakeHostTurn turn = await FakeHostTurn.StartAsync(live).ConfigureAwait(true);
    turn.Host.Script.Add(new RootTurnEvent("t", "usage",
        RootTurnUsagePayload.Serialize(UsageRow("alpha/model", "alpha/model")), 0));

    await turn.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal("alpha/model", turn.ViewModel.Status.ModelId);
  }

  [Fact]
  public async Task AUsageRowThatNamesNoServingModel_LeavesTheBarAlone()
  {
    // The provider named nothing, so the row states nothing about which entry ran. The bar
    // keeps the head it already showed rather than inventing a name.
    SessionModelPreferences live = new() { ModelIds = ["alpha/model", "beta/other"] };
    FakeHostTurn turn = await FakeHostTurn.StartAsync(live).ConfigureAwait(true);
    turn.Host.Script.Add(new RootTurnEvent("t", "usage",
        RootTurnUsagePayload.Serialize(UsageRow("alpha/model", servedModelId: null)), 0));

    await turn.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal("alpha/model", turn.ViewModel.Status.ModelId);
  }

  [Fact]
  public async Task ACompactionCall_DoesNotRenameTheSessionModel()
  {
    // The summarizer has its own model (the 'Compaction model' setting), which is not the
    // session's. A compaction row naming it must not be read as 'a backup answered'.
    SessionModelPreferences live = new() { ModelIds = ["alpha/model", "beta/other"] };
    FakeHostTurn turn = await FakeHostTurn.StartAsync(live).ConfigureAwait(true);
    turn.Host.Script.Add(new RootTurnEvent("t", "usage",
        RootTurnUsagePayload.Serialize(UsageRow("alpha/model", "summarizer/model",
            callClass: "compaction")), 0));

    await turn.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal("alpha/model", turn.ViewModel.Status.ModelId);
  }

  // ── 7 continued: the next turn's request starts at the head ────────────────

  [Fact]
  public async Task AfterAFailover_TheNextTurnStillStartsAtTheHead()
  {
    // The request each turn carries is resolved from the same preference, so a failover leaves
    // the head first. Two turns, one of them answered by the backup, and the second turn's
    // preferences are asserted through the production wire codec.
    SessionModelPreferences live = new() { ModelIds = ["alpha/model", "beta/other"] };
    FakeHostTurn turn = await FakeHostTurn.StartAsync(live).ConfigureAwait(true);
    turn.Host.Script.Add(new RootTurnEvent("t", "usage",
        RootTurnUsagePayload.Serialize(UsageRow("alpha/model", "beta/other")), 0));

    await turn.ViewModel.SubmitAsync("first").ConfigureAwait(true);
    await turn.ViewModel.SubmitAsync("second").ConfigureAwait(true);

    // The carrier the turn sent, read back through the production codec: the head is still
    // first, so the next request starts where the user put it.
    string carrier = turn.Host.LastRequest!.ModelPreferences!;
    SessionModelPreferences carried = SessionModelPreferencesJson.Deserialize(carrier).Value!;
    Assert.Equal(["alpha/model", "beta/other"], carried.ModelIds);
    Assert.Equal("beta/other", turn.ViewModel.Status.ModelId); // and the bar keeps the answer
  }

  // ── 8: the backup count ───────────────────────────────────────────────────

  [Theory]
  [InlineData(1, "")]
  [InlineData(2, "· 1 backup")]
  [InlineData(3, "· 2 backups")]
  public void TheBackupCount_CountsTheTail(int modelCount, string expected)
      => Assert.Equal(expected, AgentSessionViewModel.BackupCountText(modelCount));

  // ── 9: reopening shows the head ───────────────────────────────────────────

  [Theory]
  [InlineData("[\"alpha/model\",\"beta/other\"]", 2, "· 1 backup")]
  [InlineData("[\"alpha/model\",\"beta/other\",\"gamma/third\"]", 3, "· 2 backups")]
  [InlineData("[\"alpha/model\"]", 1, "")]
  public async Task ReopeningAWorkspace_ShowsTheHead_AndCountsTheBackups(
      string storedModels, int entryCount, string expectedBackups)
  {
    // Behaviour 9: the selection restores whole and in order, the bar names entry 1, and the
    // count matches the list. The stored value is written through the production codec, so a
    // reopen cannot be reading a shape only a test writes.
    IReadOnlyList<string> modelIds = ParseIds(storedModels);
    Assert.Equal(entryCount, modelIds.Count);
    FakePreferenceStore store = new();
    store.Stored[ModelChoiceKey] = ModelSelection.Create(modelIds).Value!.Serialize();
    SessionModelPreferences live = new();
    MainViewModel shell = new(
        (root, provider) => Task.FromResult(Result.Success(BuildSession(root, provider, live))),
        new MainViewModelOptions { Preferences = store });

    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(Root, Provider).ConfigureAwait(true);

    Assert.True(opened.IsSuccess, $"session open failed: [{opened.Error?.Code}] {opened.Error?.Message}");
    AgentTabViewModel tab = opened.Value;
    Assert.Equal(modelIds, live.ModelIds);
    Assert.Equal(modelIds[0], tab.ViewModel.Status.ModelId);
    Assert.Equal(expectedBackups, tab.ViewModel.Status.BackupCountText);
  }

  [Fact]
  public async Task Reopening_DoesNotPromoteAnAnsweringBackupIntoTheSelection()
  {
    // The persisted PIN is what actually served (a backup, here), and the stored PREFERENCE is
    // the user's ordered list. The list wins: a backup that answered one turn is not a new
    // selection, so the reopen shows the head again.
    FakePreferenceStore store = new();
    store.Stored[ModelChoiceKey] = ModelSelection.Create(
        ["alpha/model", "beta/other"]).Value!.Serialize();
    SessionModelPreferences live = new();
    MainViewModel shell = new(
        (root, provider) => Task.FromResult(Result.Success(
            BuildSession(root, provider, live, pinnedModel: "beta/other"))),
        new MainViewModelOptions { Preferences = store });

    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(Root, Provider).ConfigureAwait(true);

    Assert.True(opened.IsSuccess, $"session open failed: [{opened.Error?.Code}] {opened.Error?.Message}");
    Assert.Equal(["alpha/model", "beta/other"], live.ModelIds);
    Assert.Equal("alpha/model", opened.Value.ViewModel.Status.ModelId);
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private static IReadOnlyList<string> ParseIds(string jsonModels)
      => ModelSelection.Parse(
          /*lang=json,strict*/ $"{{\"Models\":{jsonModels}}}").Value!.ModelIds;

  private static UsageRow UsageRow(string requestedModelId, string? servedModelId,
      string callClass = "main_loop")
      => new(callClass, servedModelId ?? requestedModelId, Provider, "ws-1",
          1000, 100, 900, null, 0.000002m, 0.0000002m, 0.000004m,
          RequestedModelId: requestedModelId, ServedModelId: servedModelId);

  private static AgentSession BuildSession(string root, string provider,
      SessionModelPreferences preferences, string? pinnedModel = null)
  {
    ServiceProvider services = new ServiceCollection()
        .AddSingleton<IAgentStore>(new PinnedStore(pinnedModel, root, provider))
        .BuildServiceProvider();
    return new AgentSession(
        services,
        AgentId.NewId(),
        new Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new StubStore()),
        Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime(),
        Preferences: preferences);
  }

  /// <summary>One view-model wired the thin-client way over a scripted host channel: a
  ///     production-shaped turn, so the usage payload travels the real wire reader into the real
  ///     status bar.</summary>
  private sealed class FakeHostTurn
  {
    private FakeHostTurn(AgentSessionViewModel viewModel, FakeRootTurnChannel host)
    {
      ViewModel = viewModel;
      Host = host;
    }

    public AgentSessionViewModel ViewModel { get; }

    public FakeRootTurnChannel Host { get; }

    public static Task<FakeHostTurn> StartAsync(SessionModelPreferences preferences)
    {
      ArgumentNullException.ThrowIfNull(preferences);
      FakeRootTurnChannel host = new(["answer"], holdOpen: false);
      AgentSessionViewModel viewModel = new(
          new RecordingTurnRunner().RunAsync,
          new RootSessionLifecycle(new StubStore()),
          AgentId.NewId(),
          new Conversation(),
          provider: "OpenRouter",
          modelId: preferences.ModelIds![0],
          new AgentSessionViewModelOptions
          {
            WorkspaceRoot = Root,
            RootTurnChannel = host,
            ModelPreferences = preferences,
          });
      return Task.FromResult(new FakeHostTurn(viewModel, host));
    }
  }

  private sealed class PinnedStore(string? pinnedModel, string root, string provider)
      : IAgentStore
  {
    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(AgentRecord.Root(id, DateTimeOffset.UnixEpoch, root,
            provider, pinnedModel ?? AgentRecord.UnassignedModel)));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message,
        CancellationToken ct = default) => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id,
        IReadOnlyList<Message> messages, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id,
        CancellationToken ct = default) => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }

  private sealed class FakePreferenceStore : IAppPreferenceStore
  {
    public List<(string Key, string Value)> Writes { get; } = [];
    public List<string> Deletions { get; } = [];
    public Dictionary<string, string> Stored { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Stored.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Writes.Add((key, value));
      Stored[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
      Deletions.Add(key);
      _ = Stored.Remove(key);
      return Task.FromResult(true);
    }
  }
}
