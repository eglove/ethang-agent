using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable CA2007 // Headless UI test continuations stay on the dispatcher.
namespace eThangAgent.Desktop.Tests;

public class CatalogRefreshE2ETests
{
  private static ModelProviderEntry Cached(string id) => new(
      id, null, 0.000009m, null, 0.000010m, 200_000, 4096, true, false, true,
      90, 85, 80, null, null, null);

  [AvaloniaTheory]
  [InlineData(200)]
  [InlineData(503)]
  public async Task CachedCatalog_RendersImmediately_ThenRefreshesTheModelSection(int status)
  {
    using TestAppDatabase database = TestAppDatabase.Create();
    using MockOpenRouterServer server = new();
    server.Start();
    TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    server.CatalogGate = gate.Task;
    server.CatalogStatusCode = status;
    _ = server.ReturnsCatalog(/*lang=json,strict*/ "{\"data\":[{\"id\":\"new/model\",\"context_length\":200000,\"pricing\":{\"prompt\":\"0.000001\",\"completion\":\"0.000002\"},\"supported_parameters\":[\"tools\"]}]}");
    SqliteModelCatalogCache store = new(database.Database);
    string key = $"openrouter|{server.BaseUrl}api/v1/models";
    _ = await store.WriteAsync(key, new ModelCatalogSnapshot([Cached("old/model")], DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
    AgentSettings settings = new(new OpenRouterSettings("test-key", server.BaseUrl), new SubAgentOptions(null, 1));
    using ServiceProvider services = new ServiceCollection().AddEThangAgentCore(settings, Providers.OpenRouter,
        ModelConfig.Create("openrouter/auto", null, 256, 0.2f, 200_000).Value!,
        new AgentHostOptions(new FixedWorkspaceContext("test"), new UnrootedPathResolver()), database.Database).BuildServiceProvider();
    AgentSession session = new(services, AgentId.NewId(), services.GetRequiredService<Conversation>(),
        services.GetRequiredService<SendMessageCommandHandler>(), services.GetRequiredService<RootSessionLifecycle>(),
        services.GetRequiredService<ModelConfig>(), Directory.GetCurrentDirectory(), Providers.OpenRouter,
        services.GetRequiredService<IAgentInbox>(), services.GetRequiredService<IAgentRuntime>());
    MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(session);
    ModelSettingsViewModel vm = new(new SessionModelPreferences(), Providers.OpenRouter, _ => { },
        loadCatalog: shell.SelectedTabCatalogLoader, refreshCatalog: shell.SelectedTabCatalogRefresher);
    Task load = vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    try
    {
      await server.CatalogRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
      // Drain UI continuations, not elapsed-time polling. HTTP is held until assertions finish.
      await Dispatcher.UIThread.InvokeAsync(() => { });
      Assert.Contains(vm.ModelSection!.FilteredRows, row => row.ModelId == "old/model");
      Assert.False(load.IsCompleted);
      vm.ModelSection.SearchText = "model";
    }
    finally
    {
      _ = gate.TrySetResult();
      await load.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
    if (status == 200)
    {
      Assert.Contains(vm.ModelSection.FilteredRows, row => row.ModelId == "new/model");
      Assert.DoesNotContain(vm.ModelSection.FilteredRows, row => row.ModelId == "old/model");
      Assert.Null(vm.ModelSection.LoadError);
      Result<ModelCatalogSnapshot> persisted = await store.ReadAsync(key, TestContext.Current.CancellationToken);
      Assert.True(persisted.IsSuccess);
      Assert.Equal("new/model", Assert.Single(persisted.Value.Entries).ModelId);
    }
    else
    {
      Assert.Contains(vm.ModelSection.FilteredRows, row => row.ModelId == "old/model");
      Assert.Contains("503", vm.ModelSection.LoadError!, StringComparison.Ordinal);
    }
    Assert.Equal("model", vm.ModelSection.SearchText);
  }
}
