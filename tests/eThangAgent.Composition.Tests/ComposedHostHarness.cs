using System.Globalization;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

// A composed-session rig tears itself down best effort: a temp database still held by a dying
// connection is not a test failure (CA1031).
#pragma warning disable CA1031 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>One composed session over a real container, a real OpenRouter ACL, and a local
///     mock provider: the shape a production session has, minus the conversation-host process.
///     <para>WHY THE TURN RUNS IN THIS PROCESS: the subject is the REQUEST the session sends,
///     and a host-run turn would put the model call behind a pipe. The composition still
///     resolves the model, overlays the preferences, and builds the body - so what reaches the
///     provider is what a production turn sends.</para>
///     <para>IT BOOTS THE WAY THE FACTORY DOES: the root row is persisted through the shared
///     bootstrapper and its id published to the container, so the resolver reads the same
///     identity a production session has - and a pin recorded by a turn lands on a row that
///     exists.</para></summary>
internal sealed class ComposedHostHarness : IDisposable
{
  private readonly ServiceProvider _services;
  private readonly TestAppDatabase _db;
  private readonly string _root;
  private readonly string _workspace;

  private ComposedHostHarness(ServiceProvider services, TestAppDatabase db, string root,
      string workspace, MockProviderServer provider)
  {
    _services = services;
    _db = db;
    _root = root;
    _workspace = workspace;
    Provider = provider;
    Preference = services.GetRequiredService<SessionModelPreferences>();
    Conversation = services.GetRequiredService<Conversation>();
    Handler = services.GetRequiredService<SendMessageCommandHandler>();
  }

  /// <summary>The mock provider this session calls, recording every request it receives.</summary>
  public MockProviderServer Provider { get; }

  /// <summary>The container's live model preferences - the object the picker writes.</summary>
  public SessionModelPreferences Preference { get; }

  /// <summary>The session's conversation: the transcript a failed turn leaves its reason in.</summary>
  public Conversation Conversation { get; }

  /// <summary>The turn handler every assertion drives.</summary>
  public SendMessageCommandHandler Handler { get; }

  /// <summary>The stored preference document this session was opened from, exactly as the
  ///      desktop's save path writes it.</summary>
  public string StoredValue { get; private set; } = "";

  /// <summary>The usage rows the turns recorded, in order: which model each call ran on.</summary>
  public List<UsageRow> Usage { get; } = [];

  /// <summary>Opens the session with <paramref name="modelIds"/> as its live selection and
  ///      writes the stored preference row beside it, so an assertion can prove a failure path
  ///      left that row untouched.
  ///      <para><paramref name="models"/> is what the provider's catalog advertises. It defaults
  ///      to one windowed entry per id, because a model the catalog does not name has no context
  ///      window - which is its own named failure, not the subject here.</para></summary>
  public static async Task<ComposedHostHarness> StartAsync(IReadOnlyList<string> modelIds,
      IReadOnlyList<MockModel>? models = null)
  {
    ArgumentNullException.ThrowIfNull(modelIds);
    string root = TestTempRoot.CreateDirectory("ethang-composed-session").FullName;
    string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
    TestAppDatabase db = TestAppDatabase.Create();
    MockProviderServer provider = new(models is { Count: > 0 } known
        ? [.. known]
        : [.. modelIds.Select(MockModel.Of)]);
    provider.Start();
    provider.ServedModelId = modelIds[0];

    AgentSettings settings = new AgentSettings(
        new OpenRouterSettings("sk-or-test", provider.BaseUrl),
        new AgentRunOptions(null, 2))
        .WithWorkspaceRoot(workspace)
        .WithWorkspaceId(WorkspaceIdentity.For(workspace));

    ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create(modelIds[0], 4096, 0.7f, 32_768).Value!,
            new AgentHostOptions(FixedWorkspaceContext.ForPath(workspace), new UnrootedPathResolver()),
            db.Database)
        .AddInProcessAgentRuntime(settings)
        .BuildServiceProvider();

    services.GetRequiredService<SessionModelPreferences>().ModelIds = modelIds;
    Result<AgentId> bootstrapped = await RootSessionBootstrapper
        .PersistRootAsync(services.GetRequiredService<IAgentStore>(), workspace,
            Providers.OpenRouter, modelUsed: modelIds[0], TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(bootstrapped.IsSuccess, bootstrapped.Error?.Message);
    services.GetRequiredService<RootSessionIdentity>().Id = bootstrapped.Value;

    return new ComposedHostHarness(services, db, root, workspace, provider)
    {
      StoredValue = await StorePreferenceAsync(services, workspace, modelIds).ConfigureAwait(true),
    };
  }

  /// <summary>Sends one turn through the container's handler, collecting the usage rows it
  ///      records so a later assertion can name the model that served.
  ///      <para>THE BOUND IS THE CEILING, never a latency contract: a turn that never settles
  ///      fails the test instead of hanging the module.</para></summary>
  public async Task<Result<string>> SendAsync(string text)
      => await Handler.Handle(
          new SendMessageCommand(text),
          new TurnCallbacks(OnUsageRecorded: row => Usage.Add(row)),
          ct: TestContext.Current.CancellationToken)
          .WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);

  /// <summary>The stored preference document as it stands now, re-read from the store.</summary>
  public async Task<string?> ReadStoredValueAsync()
      => await _services.GetRequiredService<IAppPreferenceStore>()
          .GetAsync(PreferenceKey(_workspace), TestContext.Current.CancellationToken)
          .ConfigureAwait(true);

  /// <summary>Writes the selection under the key the desktop's save path uses, so this rig
  ///      reads and re-reads the production storage shape rather than a private one.</summary>
  private static async Task<string> StorePreferenceAsync(ServiceProvider services,
      string workspace, IReadOnlyList<string> modelIds)
  {
    string value = ModelSelection.Create(modelIds).Value!.Serialize();
    _ = await services.GetRequiredService<IAppPreferenceStore>()
        .SetAsync(PreferenceKey(workspace), value, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    return value;
  }

  /// <summary>The per-workspace model preference key: the provider and the workspace root,
  ///      which is the pair the desktop saves and restores.
  ///      <para>NAMED HERE RATHER THAN SHARED: the key's owner is the desktop host, a different
  ///      assembly, and reading it from there would make the storage shape a desktop
  ///      implementation detail of these tests.</para></summary>
  private static string PreferenceKey(string workspaceRoot)
      => string.Format(CultureInfo.InvariantCulture, "model_provider_choice:{0}:{1}",
          Providers.OpenRouter, workspaceRoot);

  /// <summary>Disposes the container, the mock provider, and the temp directory. Synchronous
  ///      on purpose: a test's teardown must not be the place a rig's await goes unconfigured.</summary>
  public void Dispose()
  {
    _services.Dispose();
    Provider.Dispose();
    _db.Dispose();
    try
    {
      Directory.Delete(_root, recursive: true);
    }
    catch
    {
      // Left behind deliberately: a passing test must not fail on cleanup.
    }
  }
}
