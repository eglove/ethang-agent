using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.Transport.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>A REAL conversation host, killed mid-turn, driven through the app's own turn loop.
///     <para>WHY HERE AND NOT IN Composition.Tests: the two ends of this fact live in different
///     projects. The supervisor and the wire runtime are Composition's, and the turn loop that
///     reconnects is the Desktop's, so this project - which references both - is the only place
///     the whole path can be driven: a real host process, a real attach, a real
///     <see cref="RemoteRootTurnChannel"/>, and the real <see cref="AgentSessionViewModel"/>
///     loop that resends.</para>
///     <para>WHAT IT SETTLES: a killed host does not end the turn. The app reconnects once and
///     the turn succeeds, which is the acceptance's first clause; the process the reconnect
///     started is a different process from the one that died, which is the third.</para>
///     </summary>
[Trait("Category", "Process")]
[Trait("Category", "LatencyContract")]
public sealed class SessionHostReconnectEndToEndTests
{
  /// <summary>The one model the mock catalog advertises and this session pins.</summary>
  private const string Model = "mock/sub-model";

  [Fact]
  public async Task HostKilledMidTurn_OneReconnect_AndTheTurnSucceeds()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    string root = TestTempRoot.CreateDirectory("ethang-reconnect-e2e").FullName;
    MockOpenRouterServer provider = new();
    try
    {
      string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
      provider.Start();
      // The catalog names the one model the session is pinned to, and the responses surface
      // answers every request: the replacement host has to be able to serve the resent turn
      // without any new script, because the app does not know a host changed.
      _ = provider.ReturnsCatalog(CatalogJson());
      _ = provider.Returns(CompletionJson());

      string database = Path.Combine(root, "app.db");
      AgentSettings settings = new AgentSettings(
          new OpenRouterSettings("sk-or-test", provider.BaseUrl),
          new AgentRunOptions(null, 2)).WithWorkspaceRoot(workspace);
      string identity = Guid.NewGuid().ToString();
      RemoteHostSupervisor supervisor = new(identity,
          Path.Combine(root, RemoteHostSupervisor.ScratchFolderForConversation(identity)),
          settings, database, _ => { });
      RemoteAgentRuntime runtime = new();
      RemoteRootTurnChannel channel = new(runtime, () => supervisor);
      AgentId rootId = AgentId.NewId();
      ServiceProvider services = new ServiceCollection()
          .AddSingleton<IAgentStore>(new NoStore())
          .AddSingleton<SessionNoticeSink>()
          .BuildServiceProvider();
      try
      {
        _ = await supervisor.AttachAsync(runtime, ct).ConfigureAwait(true);
        int? first = supervisor.HostProcessId;
        Assert.True(first is not null, "the attach must have started a host.");

        AgentSessionViewModel vm = NewViewModel(services, rootId, workspace, channel);

        // The host dies while the turn is in flight: the first attempt's send fails against a
        // connection whose peer is gone.
        Task turn = vm.SubmitAsync("what is true?");
        supervisor.StopHost();
        await turn.WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true);
        await vm.WaitForTurnAsync().WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true);

        Assert.Contains(vm.Transcript.Notices, n => n.Contains("reconnect", StringComparison.Ordinal));
        // THE TURN SUCCEEDED, not merely that a reconnect happened: the resent turn was served by
        // the replacement host, so its answer is on screen and no HostUnavailable line is.
        Assert.True(vm.Transcript.AssistantText.Contains("true", StringComparison.Ordinal),
            "notices: " + string.Join(" | ", vm.Transcript.Notices));
        Assert.DoesNotContain(vm.Transcript.Notices, n => n.Contains("Error [HostUnavailable]", StringComparison.Ordinal));
        int? second = supervisor.HostProcessId;
        Assert.True(second is not null, "the reconnect must have started a replacement host.");
        Assert.NotEqual(first, second);
        Assert.True(runtime.IsConnected, "the reconnected runtime must hold a live connection.");
      }
      finally
      {
        await supervisor.DisposeAsync().ConfigureAwait(true);
        await services.DisposeAsync().ConfigureAwait(true);
      }
    }
    finally
    {
      provider.Dispose();
      try
      {
        Directory.Delete(root, recursive: true);
      }
      catch (IOException)
      {
        // A host still releasing its SQLite handle is the expected transient.
      }
    }
  }

  /// <summary>A session view-model over the given channel: the loop under test is the
  ///     production one, and only the streaming sink is replaced so events apply inline.</summary>
  private static AgentSessionViewModel NewViewModel(IServiceProvider services, AgentId rootId,
      string workspace, IRootTurnChannel channel)
  {
    AgentSessionViewModel? vm = null;
    Func<UiStreamEvent, Task> sink = new(evt => vm is null
        ? Task.CompletedTask
        : vm.ApplyUiStreamEventAsync(evt));
    vm = new AgentSessionViewModel(
        (_, _, _, _) => throw new InvalidOperationException("a host turn must never run in process."),
        new RootSessionLifecycle(services.GetRequiredService<IAgentStore>()),
        rootId,
        new Conversation(),
        provider: Providers.OpenRouter,
        modelId: Model,
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = workspace,
          RootTurnChannel = channel,
          // The session's model rides every turn's carrier to the host, which is the only place
          // the model resolves: a session that named none would be refused by the replacement
          // host no matter how healthy the connection is.
          ModelPreferences = new SessionModelPreferences { ModelIds = [Model] },
          UiStreamSink = sink,
        });
    return vm;
  }

  private static string CatalogJson() => /*lang=json,strict*/
      "{ \"data\": [ { \"id\": \"mock/sub-model\", \"pricing\": { \"prompt\": \"0.000001\", \"completion\": \"0.000002\" }, \"context_length\": 32768, \"top_provider\": { \"max_completion_tokens\": 8192 }, \"architecture\": { \"modality\": \"text->text\" } } ] }";

  private static string CompletionJson() => /*lang=json,strict*/
      "{ \"id\": \"resp_1\", \"model\": \"mock/sub-model\", \"output\": [ { \"type\": \"message\", \"role\": \"assistant\", \"content\": [ { \"type\": \"output_text\", \"text\": \"true\" } ] } ], \"usage\": { \"input_tokens\": 5, \"output_tokens\": 1, \"total_tokens\": 6 } }";

  /// <summary>A store that persists nothing: this test asserts the turn's PATH, and the real
  ///     store would add a database file every host would have to share.</summary>
  private sealed class NoStore : IAgentStore
  {
    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages,
        CancellationToken ct = default) => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRecord>(new DomainError("NotFound", "no record.")));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }
}
