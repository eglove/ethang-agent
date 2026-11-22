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

/// <summary>A throttled model is dropped and the turn is resent on the backup THROUGH A REAL
///     CONVERSATION HOST, which is the only path the product runs.
///     <para>WHY THIS EXISTS: a host turn reports a failed turn as a SUCCESSFUL transport turn
///     whose payload is the typed failure line, so a drop that read only a failed Result would
///     pass every in-process test and never fire for the user. The other drop tests drive a
///     scripted channel; this one proves the shape the host actually sends.</para></summary>
[Trait("Category", "Process")]
public sealed class ModelDropThroughHostTests
{
  private const string Head = "mock/model-a";
  private const string Backup = "mock/model-b";

  [Fact]
  public async Task AThrottledModel_IsDropped_AndTheHostServesTheBackup()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    string root = TestTempRoot.CreateDirectory("ethang-model-drop-host").FullName;
    MockOpenRouterServer provider = new();
    try
    {
      string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
      provider.Start();
      _ = provider.ReturnsCatalog(CatalogJson());
      // The head is throttled and the backup answers, both scripted BY MODEL so the order the
      // two requests arrive in cannot decide which one the mock serves. The head is throttled
      // once per provider attempt: the provider spends its own retries on the same model, and
      // only the failure that survives them reaches the turn.
      _ = provider.ReturnsStatusForModel(Head, 429, 429, 429, 429);
      _ = provider.ReturnsForModel(Backup, CompletionJson());

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

        AgentSessionViewModel vm = NewViewModel(services, rootId, workspace, channel);

        await vm.SubmitAsync("what is true?").WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true);
        await vm.WaitForTurnAsync().WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true);

        // The head was dropped and the turn was resent: the backup's answer is on screen.
        Assert.True(vm.Transcript.Notices.Contains(
                $"{Head} ({ModelVendors.DisplayName(ModelVendors.OpenRouter)}) was rate limited; "
                + "removed for this session - 1 model left", StringComparer.Ordinal),
            "notices: " + string.Join(" | ", vm.Transcript.Notices));
        Assert.True(vm.Transcript.AssistantText.Contains("served by the backup", StringComparison.Ordinal),
            "notices: " + string.Join(" | ", vm.Transcript.Notices));
        Assert.Equal([Backup], vm.Status.SurvivingModelIds);

        // The resend really retargeted: the head was asked first, and the LAST model the
        // provider saw is the backup. The head appears more than once because the provider
        // spends its own retries on it before the turn ever sees the throttle.
        string[] asked = [.. provider.RequestBodies
            .Select(MockOpenRouterServer.TryGetRequestModel)
            .Where(model => model is not null)
            .Select(model => model!)];
        Assert.Equal(Head, asked[0], StringComparer.Ordinal);
        Assert.Equal(Backup, asked[^1], StringComparer.Ordinal);
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

  /// <summary>A REFUSED CREDENTIAL STOPS THE TURN AND THE PROVIDER'S OWN WORDS REACH THE USER.
  ///     <para>A 403 is a configuration fault about the account, not a busy model: the walk does
  ///     not advance, the backup never serves, and the model that was refused is NOT removed from
  ///     the session's list - dropping it would hide a broken credential behind a silent
  ///     fall-through.</para></summary>
  [Fact]
  public async Task ARefusedCredential_StopsTheTurn_AndTheProvidersOwnTextIsInTheTranscript()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    string root = TestTempRoot.CreateDirectory("ethang-refused-credential-host").FullName;
    MockOpenRouterServer provider = new();
    try
    {
      string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
      provider.Start();
      _ = provider.ReturnsCatalog(CatalogJson());
      // The head is refused for its credential; the backup would answer if it were ever asked.
      _ = provider.ReturnsStatusForModel(Head, 403, 403, 403, 403);
      _ = provider.ReturnsForModel(Backup, CompletionJson());

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

        AgentSessionViewModel vm = NewViewModel(services, rootId, workspace, channel,
            [new ModelSelectionEntry(Head, ModelVendors.OpenRouter), new ModelSelectionEntry(Backup)]);

        await vm.SubmitAsync("what is true?").WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true);
        await vm.WaitForTurnAsync().WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true);

        // The provider's own text, verbatim, including its status line.
        string transcript = string.Join(" | ", vm.Transcript.Notices);
        Assert.Contains("scripted 403", transcript, StringComparison.Ordinal);
        // The refused model is still the session's: a broken credential is not a busy pool.
        Assert.Equal([Head, Backup], vm.Status.SurvivingModelIds);
        // The backup was never asked: the walk stops rather than falling through.
        string[] asked = [.. provider.RequestBodies
            .Select(MockOpenRouterServer.TryGetRequestModel)
            .Where(model => model is not null)
            .Select(model => model!)];
        Assert.DoesNotContain(Backup, asked);
        Assert.DoesNotContain("served by the backup", vm.Transcript.AssistantText,
            StringComparison.Ordinal);
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

  /// <summary>THE STATUS LINE NAMES THE VENDOR THAT SERVED. A model id alone cannot say which
  ///     vendor answered, and the same id can be served by two vendors at different prices, so
  ///     the bar states the vendor the serving entry named.</summary>
  [Fact]
  public async Task TheStatusLine_NamesTheVendorOfTheModelThatServed()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    string root = TestTempRoot.CreateDirectory("ethang-serving-vendor-host").FullName;
    MockOpenRouterServer provider = new();
    try
    {
      string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
      provider.Start();
      _ = provider.ReturnsCatalog(CatalogJson());
      // The answer names the model it came from, which is what the usage row reports and what
      // the status bar then names.
      _ = provider.ReturnsForModel(Head, CompletionJsonFor(Head));

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

        AgentSessionViewModel vm = NewViewModel(services, rootId, workspace, channel,
            [new ModelSelectionEntry(Head, ModelVendors.OpenRouter)]);

        await vm.SubmitAsync("what is true?").WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true);
        await vm.WaitForTurnAsync().WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true);

        Assert.Equal(ModelVendors.DisplayName(ModelVendors.OpenRouter), vm.Status.ServingVendor);
        // The model cell still names the model, and nothing about it changed.
        Assert.Equal(Head, vm.Status.ModelId, StringComparer.Ordinal);
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
      string workspace, IRootTurnChannel channel, IReadOnlyList<ModelSelectionEntry>? entries = null)
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
        modelId: Head,
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = workspace,
          RootTurnChannel = channel,
          // The session's whole list rides every turn's carrier to the host, which is where the
          // model resolves: a carrier naming one model would leave nothing to fall back to.
          ModelPreferences = new SessionModelPreferences
          {
            Entries = entries ?? [new ModelSelectionEntry(Head), new ModelSelectionEntry(Backup)],
          },
          UiStreamSink = sink,
        });
    return vm;
  }

  private static string CatalogJson() => /*lang=json,strict*/
      "{ \"data\": [ { \"id\": \"mock/model-a\", \"pricing\": { \"prompt\": \"0.000001\", \"completion\": \"0.000002\" }, \"context_length\": 32768, \"top_provider\": { \"max_completion_tokens\": 8192 }, \"architecture\": { \"modality\": \"text->text\" } }, { \"id\": \"mock/model-b\", \"pricing\": { \"prompt\": \"0.000001\", \"completion\": \"0.000002\" }, \"context_length\": 32768, \"top_provider\": { \"max_completion_tokens\": 8192 }, \"architecture\": { \"modality\": \"text->text\" } } ] }";

  /// <summary>The completion a model answers with, naming ITSELF as the model that served: the
  ///     usage row reports that name, which is what the status bar then displays.</summary>
  private static string CompletionJsonFor(string model) =>
      "{ \"id\": \"resp_1\", \"model\": \"" + model
      + "\", \"output\": [ { \"type\": \"message\", \"role\": \"assistant\", \"content\": "
      + "[ { \"type\": \"output_text\", \"text\": \"served by the backup\" } ] } ], "
      + "\"usage\": { \"input_tokens\": 5, \"output_tokens\": 1, \"total_tokens\": 6 } }";

  private static string CompletionJson() => /*lang=json,strict*/
      "{ \"id\": \"resp_1\", \"model\": \"mock/model-b\", \"output\": [ { \"type\": \"message\", \"role\": \"assistant\", \"content\": [ { \"type\": \"output_text\", \"text\": \"served by the backup\" } ] } ], \"usage\": { \"input_tokens\": 5, \"output_tokens\": 1, \"total_tokens\": 6 } }";

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
