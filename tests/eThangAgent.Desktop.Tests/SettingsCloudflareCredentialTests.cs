using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using TextBlock = Avalonia.Controls.TextBlock;

namespace eThangAgent.Desktop.Tests;

/// <summary>The Cloudflare credential as the app's own surfaces hold it: the two fields in the
///     Settings dialog, what a save writes, what a reopen shows, and which vendors the shell
///     offers once it is configured.
///     <para>THE CREDENTIAL IS APP-WIDE AND PROTECTED, on exactly the terms the OpenRouter key
///     already has: both halves are stored through the key protector, a blank field clears its
///     own key, and the vendor appears in the shell's list only while BOTH halves are present.
///     </para></summary>
[Trait("Category", "Process")]
public class SettingsCloudflareCredentialTests
{
  private const string AccountId = "0123456789abcdef0123456789abcdef";
  private const string ApiToken = "cf-token-abc";

  // ── the dialog's own fields ───────────────────────────────────────────────

  [AvaloniaFact]
  public void TheDialog_ShowsBothFields_Masked_AndSaysTheCredentialIsConfigured()
  {
    SettingsWindow window = new(new SettingsValues
    {
      CloudflareAccountId = AccountId,
      CloudflareApiToken = ApiToken,
    });
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");
    tabs.SelectedIndex = 0;
    Dispatcher.UIThread.RunJobs();

    TextBox accountId = window.GetControl<TextBox>("CloudflareAccountIdBox");
    TextBox token = window.GetControl<TextBox>("CloudflareApiTokenBox");
    Assert.Equal(AccountId, accountId.Text);
    Assert.Equal(ApiToken, token.Text);
    // Masked exactly as the OpenRouter key is: the same password character, from the same
    // reveal toggle, so a credential is never on screen in the clear by default.
    Assert.NotEqual(default, accountId.PasswordChar);
    Assert.NotEqual(default, token.PasswordChar);
    Assert.Equal("Cloudflare is configured",
        window.GetControl<TextBlock>("CloudflareStatusText").Text);
  }

  [AvaloniaFact]
  public void TheDialog_SaysWhichHalfIsMissing_WhenOnlyOneIsFilledIn()
  {
    SettingsWindow window = new(new SettingsValues { CloudflareAccountId = AccountId });
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");
    tabs.SelectedIndex = 0;
    Dispatcher.UIThread.RunJobs();

    Assert.Equal("Cloudflare needs both an account id and an API token",
        window.GetControl<TextBlock>("CloudflareStatusText").Text);
  }

  [Fact]
  public void TheSave_CarriesBothFields_Trimmed_AndABlankFieldAsCleared()
  {
    SettingsViewModel vm = new(new SettingsValues())
    {
      CloudflareAccountId = "  " + AccountId + "  ",
      CloudflareApiToken = "  " + ApiToken + "  ",
    };
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;

    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.Equal(AccountId, saved.CloudflareAccountId);
    Assert.Equal(ApiToken, saved.CloudflareApiToken);

    SettingsViewModel cleared = new(new SettingsValues
    {
      CloudflareAccountId = AccountId,
      CloudflareApiToken = ApiToken,
    });
    SettingsUpdate? clearedSave = null;
    cleared.SaveRequested += (_, update) => clearedSave = update;
    cleared.CloudflareApiToken = string.Empty;
    cleared.SaveCommand.Execute(null);

    Assert.NotNull(clearedSave);
    Assert.Null(clearedSave.CloudflareApiToken);
    Assert.Equal(AccountId, clearedSave.CloudflareAccountId);
  }

  [Fact]
  public void AFieldWithInternalWhitespace_IsRefused_AndSaveIsDisabled()
  {
    SettingsViewModel vm = new(new SettingsValues())
    {
      CloudflareApiToken = "cf token with spaces",
    };

    Assert.Equal("API keys cannot contain whitespace.", vm.ValidationError);
    Assert.False(vm.CanSave);
  }

  // ── the save path ─────────────────────────────────────────────────────────

  [Fact]
  public async Task ASave_StoresBothHalves_Protected_AndTheShellOffersCloudflare()
  {
    FakePreferenceStore preferences = new();
    FakeKeyProtector protector = new();
    MainViewModel vm = Shell(preferences, protector, openRouter: "sk-or-v1-abc");
    Assert.Equal([Providers.OpenRouter], vm.AvailableProviders.Select(p => p.Id));

    await vm.ApplySettingsAsync(new SettingsUpdate("sk-or-v1-abc", CommitStyle.Conventional,
        CloudflareAccountId: AccountId, CloudflareApiToken: ApiToken));

    Assert.Contains(preferences.Writes, w => w.Key == CloudflareSettings.AccountIdPreferenceKey
        && w.Value == "protected:" + AccountId);
    Assert.Contains(preferences.Writes, w => w.Key == CloudflareSettings.ApiTokenPreferenceKey
        && w.Value == "protected:" + ApiToken);
    // The live settings carry the credential, so the next agent opened serves Cloudflare rows.
    Assert.Equal(AccountId, vm.ConfiguredCloudflareAccountId);
    Assert.Equal(ApiToken, vm.ConfiguredCloudflareApiToken);
    Assert.Equal([Providers.OpenRouter, Providers.Cloudflare],
        vm.AvailableProviders.Select(p => p.Id));
  }

  [Fact]
  public async Task ClearingTheToken_DeletesItsKey_AndDropsCloudflareFromTheOfferedVendors()
  {
    FakePreferenceStore preferences = new();
    FakeKeyProtector protector = new();
    MainViewModel vm = Shell(preferences, protector, openRouter: "sk-or-v1-abc",
        accountId: AccountId, apiToken: ApiToken);
    Assert.Equal([Providers.OpenRouter, Providers.Cloudflare],
        vm.AvailableProviders.Select(p => p.Id));

    await vm.ApplySettingsAsync(new SettingsUpdate("sk-or-v1-abc", CommitStyle.Conventional,
        CloudflareAccountId: AccountId, CloudflareApiToken: null));

    Assert.Contains(preferences.Deletions, k => k == CloudflareSettings.ApiTokenPreferenceKey);
    Assert.Null(vm.ConfiguredCloudflareApiToken);
    // A half credential configures nothing: Cloudflare rows are gone from the next open.
    Assert.Equal([Providers.OpenRouter], vm.AvailableProviders.Select(p => p.Id));
  }

  [Fact]
  public async Task ClearingBothHalves_RemovesTheCredentialEntirely()
  {
    FakePreferenceStore preferences = new();
    FakeKeyProtector protector = new();
    MainViewModel vm = Shell(preferences, protector, openRouter: "sk-or-v1-abc",
        accountId: AccountId, apiToken: ApiToken);

    await vm.ApplySettingsAsync(new SettingsUpdate("sk-or-v1-abc", CommitStyle.Conventional,
        CloudflareAccountId: null, CloudflareApiToken: null));

    Assert.Contains(preferences.Deletions, k => k == CloudflareSettings.AccountIdPreferenceKey);
    Assert.Contains(preferences.Deletions, k => k == CloudflareSettings.ApiTokenPreferenceKey);
    // Both halves are gone, so the credential is absent rather than half-filled - the
    // distinction the settings record itself makes.
    Assert.Null(vm.ConfiguredCloudflareAccountId);
    Assert.Null(vm.ConfiguredCloudflareApiToken);
    Assert.Equal([Providers.OpenRouter], vm.AvailableProviders.Select(p => p.Id));
  }

  [Fact]
  public async Task WithoutAProtector_NeitherHalfIsEverStoredInTheClear()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = Shell(preferences, protector: null, openRouter: "sk-or-v1-abc");

    await vm.ApplySettingsAsync(new SettingsUpdate("sk-or-v1-abc", CommitStyle.Conventional,
        CloudflareAccountId: AccountId, CloudflareApiToken: ApiToken));

    Assert.DoesNotContain(preferences.Writes, w => w.Key == CloudflareSettings.AccountIdPreferenceKey);
    Assert.DoesNotContain(preferences.Writes, w => w.Key == CloudflareSettings.ApiTokenPreferenceKey);
    // The in-memory surface still reflects the edit; only persistence is skipped.
    Assert.Equal(ApiToken, vm.ConfiguredCloudflareApiToken);
  }

  [Fact]
  public void AStoredCredential_IsWhatTheSettingsDialogOpensWith()
  {
    FakePreferenceStore preferences = new();
    FakeKeyProtector protector = new();
    MainViewModel vm = Shell(preferences, protector, openRouter: "sk-or-v1-abc",
        accountId: AccountId, apiToken: ApiToken);

    SettingsValues prefill = DesktopHost.SettingsValuesFrom(Bootstrap(vm));

    Assert.Equal(AccountId, prefill.CloudflareAccountId);
    Assert.Equal(ApiToken, prefill.CloudflareApiToken);
    // The OpenRouter key is unchanged in place: the new section sits beside it, not over it.
    Assert.Equal("sk-or-v1-abc", prefill.OpenRouterKey);
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private static MainViewModel Shell(IAppPreferenceStore preferences, IApiKeyProtector? protector,
      string? openRouter = null, string? accountId = null, string? apiToken = null)
  {
    AgentSettings settings = new(
        new OpenRouterSettings(openRouter, new Uri("https://openrouter.test")),
        new AgentDomain.AgentRunOptions(null, 2),
        Cloudflare: accountId is null && apiToken is null
            ? null
            : new CloudflareSettings(accountId, apiToken));
    return new(null,
        new MainViewModelOptions
        {
          Preferences = preferences,
          Settings = settings,
          SessionFactory = new AgentSessionFactory(settings),
          ApiKeyProtector = protector,
        });
  }

  /// <summary>The bootstrap the dialog's prefill is built from, over the same settings and
  ///     store the shell holds.</summary>
  private static DesktopBootstrap Bootstrap(MainViewModel vm)
  {
    AgentSettings settings = new(
        new OpenRouterSettings(vm.ConfiguredOpenRouterKey, new Uri("https://openrouter.test")),
        new AgentDomain.AgentRunOptions(null, 2),
        Cloudflare: new CloudflareSettings(vm.ConfiguredCloudflareAccountId,
            vm.ConfiguredCloudflareApiToken));
    return new DesktopBootstrap(
        new AgentSessionFactory(settings),
        settings,
        Providers.OpenRouter,
        new FakePreferenceStore(),
        new FakeKeyProtector(),
        null!,
        CommitStyle.Conventional,
        null!,
        null!,
        false,
        _ => null!,
        null!,
        null!,
        null,
        null,
        [],
        []);
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

  private sealed class FakeKeyProtector : IApiKeyProtector
  {
    public string Protect(string apiKey) => $"protected:{apiKey}";

    public string? Unprotect(string storedValue)
        => storedValue.StartsWith("protected:", StringComparison.Ordinal)
            ? storedValue["protected:".Length..]
            : null;
  }
}
