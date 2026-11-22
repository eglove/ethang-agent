using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>No surface asks which provider a session uses: a session's models come from its
///     ordered list and every row names the vendor that serves it, so a provider is a fact about
///     a model rather than a choice made before one.
///     <para>THE RETIRED PREFERENCE IS READ AND IGNORED. A workspace whose stored
///     <c>active_provider</c> row names a provider still opens, the row is left alone rather than
///     deleted or reused, and nothing writes it again - a key with no reader that starts being
///     written for another purpose is a setting a user cannot see or clear.</para></summary>
[Trait("Category", "Process")]
public class ProviderDropdownRemovalTests
{
  private const string Root = @"C:\work\provider-dropdown";

  // ── nothing in Settings selects a provider ────────────────────────────────

  [AvaloniaFact]
  public void TheSettingsWindow_HoldsNoControlBoundToAProviderList()
  {
    SettingsWindow window = new(new SettingsValues { OpenRouterKey = "sk-or-v1-abc" });
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");

    List<string> offenders = [];
    for (int index = 0; index < tabs.Items.Count; index++)
    {
      tabs.SelectedIndex = index;
      Dispatcher.UIThread.RunJobs();
      offenders.AddRange(window.GetVisualDescendants()
          .OfType<Control>()
          .Select(control => (control, source: ItemsSourceOf(control)))
          .Where(pair => pair.source is not null && IsProviderList(pair.source))
          .Select(pair => pair.control.GetType().Name));
    }

    Assert.Empty(offenders);
  }

  /// <summary>No member of the settings view-model or the update it emits carries a provider
  ///     choice. The sign-in seam is the one provider-shaped member left, and it is a base URL's
  ///     sign-in rather than a selection.</summary>
  [Fact]
  public void TheSettingsViewModel_CarriesNoProviderChoice()
  {
    string[] selectionMembers = [.. typeof(SettingsViewModel)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        .Select(property => property.Name)
        .Where(name => name.Contains("Provider", StringComparison.Ordinal))
        .Where(name => name is not ("ProviderSignIn" or "ProviderSignInFactory"))];

    Assert.Empty(selectionMembers);
    Assert.DoesNotContain(typeof(SettingsUpdate).GetProperties(),
        property => property.Name.Contains("Provider", StringComparison.Ordinal));
  }

  // ── a stored provider preference neither breaks an open nor is rewritten ──

  [Fact]
  public async Task AWorkspaceWithAStoredProviderPreference_Opens_AndTheStoredValueSurvives()
  {
    FakePreferenceStore preferences = new();
    preferences.Stored[Providers.PreferenceKey] = Providers.OpenRouter;
    MainViewModel vm = Shell(preferences);

    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(Root, Providers.OpenRouter);

    Assert.True(opened.IsSuccess, $"[{opened.Error?.Code}] {opened.Error?.Message}");
    // The row is neither deleted nor rewritten: it is a value this build does not read, and
    // leaving it untouched is what keeps a downgrade from losing what the user had.
    Assert.Equal(Providers.OpenRouter, preferences.Stored[Providers.PreferenceKey]);
    Assert.DoesNotContain(preferences.Writes, w => w.Key == Providers.PreferenceKey);
    Assert.DoesNotContain(preferences.Deletions, k => k == Providers.PreferenceKey);
  }

  [Fact]
  public async Task AStoredProviderPreferenceNamingAnUnknownProvider_StillOpens()
  {
    FakePreferenceStore preferences = new();
    preferences.Stored[Providers.PreferenceKey] = "zai";
    MainViewModel vm = Shell(preferences);

    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(Root, Providers.OpenRouter);

    Assert.True(opened.IsSuccess, $"[{opened.Error?.Code}] {opened.Error?.Message}");
    Assert.Equal("zai", preferences.Stored[Providers.PreferenceKey]);
  }

  [Fact]
  public async Task OpeningAWorkspace_WritesNoProviderPreference_AtAll()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = Shell(preferences);

    _ = await vm.OpenAgentAsync(Root, Providers.OpenRouter);

    Assert.DoesNotContain(preferences.Writes, w => w.Key == Providers.PreferenceKey);
  }

  // ── the shell's vendor list is a gate, not a selection ────────────────────

  [Fact]
  public void BothConfiguredVendors_AreOffered_AndTheFirstOneIsPreferred()
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-v1-abc", new Uri("https://openrouter.test")),
        new AgentDomain.AgentRunOptions(null, 2),
        Cloudflare: new CloudflareSettings("account", "token"));
    MainViewModel vm = new(null,
        new MainViewModelOptions
        {
          Settings = settings,
          SessionFactory = new AgentSessionFactory(settings),
        });

    Assert.Equal([Providers.OpenRouter, Providers.Cloudflare],
        vm.AvailableProviders.Select(p => p.Id));
    Assert.Equal(Providers.OpenRouter, vm.PreferredProviderId);
    Assert.True(vm.HasConfiguredProvider);
  }

  [Fact]
  public void NoConfiguredVendor_OffersNothing_AndGatesOpeningAnAgent()
  {
    AgentSettings settings = new(
        new OpenRouterSettings(null, new Uri("https://openrouter.test")),
        new AgentDomain.AgentRunOptions(null, 2));
    MainViewModel vm = new(null,
        new MainViewModelOptions
        {
          Settings = settings,
          SessionFactory = new AgentSessionFactory(settings),
        });

    Assert.Empty(vm.AvailableProviders);
    Assert.False(vm.HasConfiguredProvider);
    Assert.False(vm.AddAgentCommand.CanExecute(null));
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private static object? ItemsSourceOf(Control control) => control switch
  {
    ComboBox combo => combo.ItemsSource,
    ListBox list => list.ItemsSource,
    ItemsControl items => items.ItemsSource,
    _ => null,
  };

  private static bool IsProviderList(object source) => source is IEnumerable<ProviderOption>
      || (source is IEnumerable<object> items && items.Any(item => item is ProviderOption));

  private static MainViewModel Shell(IAppPreferenceStore preferences)
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-v1-abc", new Uri("https://openrouter.test")),
        new AgentDomain.AgentRunOptions(null, 2));
    return new((root, provider) => Task.FromResult(Result.Success(Session(root, provider))),
        new MainViewModelOptions
        {
          Preferences = preferences,
          Settings = settings,
          SessionFactory = new AgentSessionFactory(settings),
        });
  }

  private static AgentSession Session(string root, string provider)
  {
    ServiceProvider services = new ServiceCollection()
        .AddSingleton<AgentDomain.IAgentStore>(new TestFixtures.StubStore())
        .BuildServiceProvider();
    return new AgentSession(
        services,
        AgentDomain.AgentId.NewId(),
        new ConversationDomain.Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
        Inbox: new AgentDomain.BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime(),
        Preferences: null);
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
