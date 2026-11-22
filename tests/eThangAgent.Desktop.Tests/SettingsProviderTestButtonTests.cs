using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Button = Avalonia.Controls.Button;
using TextBlock = Avalonia.Controls.TextBlock;

namespace eThangAgent.Desktop.Tests;

/// <summary>The Test button under each provider's key inputs: it pulls that provider's model
///     list with what is in the fields, states what the credential unlocks, and is offered only
///     when there is a credential to test.
///     <para>THE PULL IS THE TEST. A key that answers with models is proven, and the same pull is
///     what puts those models in the model search - which is why the button exists at all, and
///     why it is not a separate 'is this key valid' call.</para></summary>
[Trait("Category", "Process")]
public class SettingsProviderTestButtonTests
{
  private static ProviderProbeResult Probe(string vendor, int models) =>
      new(vendor, [.. Enumerable.Range(0, models).Select(i => Entry($"{vendor}/model-{i}"))]);

  private static ModelProviderEntry Entry(string id) => new(
      id, null, 0.000001m, null, 0.000002m, 8192, 4096, true, false, true,
      null, null, null, null, null, null);

  // ── the button's availability ─────────────────────────────────────────────

  [Fact]
  public void TheOpenRouterTest_IsOfferedOnlyWithAKeyInTheField()
  {
    SettingsViewModel vm = new(new SettingsValues { ProviderProbe = NeverCalled });
    Assert.False(vm.CanTestOpenRouter);

    vm.OpenRouterKey = "sk-or-v1-abc";

    Assert.True(vm.CanTestOpenRouter);
  }

  [Fact]
  public void TheCloudflareTest_IsOfferedOnlyWithBothHalves()
  {
    SettingsViewModel vm = new(new SettingsValues { ProviderProbe = NeverCalled });
    Assert.False(vm.CanTestCloudflare);

    vm.CloudflareAccountId = "acct-1";
    Assert.False(vm.CanTestCloudflare);

    vm.CloudflareApiToken = "token-1";
    Assert.True(vm.CanTestCloudflare);
  }

  [Fact]
  public void AHostWithNoProbe_OffersNoTestAtAll()
  {
    SettingsViewModel vm = new(new SettingsValues())
    {
      OpenRouterKey = "sk-or-v1-abc",
      CloudflareAccountId = "acct-1",
      CloudflareApiToken = "token-1",
    };

    Assert.False(vm.CanTestOpenRouter);
    Assert.False(vm.CanTestCloudflare);
  }

  // ── the pull ──────────────────────────────────────────────────────────────

  [Fact]
  public async Task ACloudflareTest_PullsWithTheFields_AndStatesTheModelCount()
  {
    List<(string Vendor, string? AccountId, string? Token)> calls = [];
    SettingsViewModel vm = new(new SettingsValues
    {
      ProviderProbe = (vendor, accountId, token, _, _) =>
      {
        calls.Add((vendor, accountId, token));
        return Task.FromResult(Result.Success(Probe(ModelVendors.Cloudflare, 3)));
      },
    })
    {
      CloudflareAccountId = "acct-1",
      CloudflareApiToken = "token-1",
    };

    await vm.TestCloudflareCommand.ExecuteAsync(null);

    (string vendor, string? accountId, string? token) = Assert.Single(calls);
    Assert.Equal(ModelVendors.Cloudflare, vendor);
    Assert.Equal("acct-1", accountId);
    Assert.Equal("token-1", token);
    Assert.Equal("Cloudflare works: 3 models are available.", vm.CloudflareTestResult);
    Assert.True(vm.HasCloudflareTestResult);
  }

  [Fact]
  public async Task AnOpenRouterTest_PullsFromTheBaseUrlInTheForm()
  {
    Uri? used = null;
    SettingsViewModel vm = new(new SettingsValues
    {
      ProviderProbe = (_, _, _, baseUrl, _) =>
      {
        used = baseUrl;
        return Task.FromResult(Result.Success(Probe(ModelVendors.OpenRouter, 1)));
      },
    })
    {
      OpenRouterKey = "sk-or-v1-abc",
      OpenRouterBaseUrlText = "https://proxy.test",
    };

    await vm.TestOpenRouterCommand.ExecuteAsync(null);

    Assert.Equal(new Uri("https://proxy.test"), used);
    Assert.Equal("OpenRouter works: 1 model is available.", vm.OpenRouterTestResult);
  }

  [Fact]
  public async Task AFailedTest_StatesTheProvidersOwnReason()
  {
    SettingsViewModel vm = new(new SettingsValues
    {
      ProviderProbe = (_, _, _, _, _) => Task.FromResult(Result.Failure<ProviderProbeResult>(
          new DomainError("ProviderUnauthorized", "Cloudflare refused the credential (403): Invalid API Token"))),
    })
    {
      CloudflareAccountId = "acct-1",
      CloudflareApiToken = "token-wrong",
    };

    await vm.TestCloudflareCommand.ExecuteAsync(null);

    Assert.NotNull(vm.CloudflareTestResult);
    Assert.Contains("Invalid API Token", vm.CloudflareTestResult, StringComparison.Ordinal);
  }

  [Fact]
  public async Task EditingTheKey_RetiresTheVerdictBesideIt()
  {
    SettingsViewModel vm = new(new SettingsValues
    {
      ProviderProbe = (_, _, _, _, _) =>
          Task.FromResult(Result.Success(Probe(ModelVendors.Cloudflare, 2))),
    })
    {
      CloudflareAccountId = "acct-1",
      CloudflareApiToken = "token-1",
    };
    await vm.TestCloudflareCommand.ExecuteAsync(null);
    Assert.True(vm.HasCloudflareTestResult);

    vm.CloudflareApiToken = "token-2";

    // A result describes the key that produced it; leaving it beside a value it never tested
    // would claim a verdict about a credential nobody tried.
    Assert.False(vm.HasCloudflareTestResult);
  }

  // ── the dialog's chrome ───────────────────────────────────────────────────

  [AvaloniaFact]
  public void TheDialog_ShowsTheTestButtonBesideEachProvider()
  {
    SettingsWindow window = new(new SettingsValues
    {
      OpenRouterKey = "sk-or-v1-abc",
      CloudflareAccountId = "acct-1",
      CloudflareApiToken = "token-1",
      ProviderProbe = NeverCalled,
    });
    window.Show();
    TabControl tabs = window.GetControl<TabControl>("SettingsTabs");
    tabs.SelectedIndex = 0;
    Dispatcher.UIThread.RunJobs();

    Assert.True(window.GetControl<Button>("TestOpenRouterButton").IsVisible);
    Assert.True(window.GetControl<Button>("TestCloudflareButton").IsVisible);
    // The result line exists but says nothing until a test runs.
    Assert.False(window.GetControl<TextBlock>("CloudflareTestResultText").IsVisible);
  }

  private static Task<Result<ProviderProbeResult>> NeverCalled(string vendor, string? accountId,
      string? apiToken, Uri? baseUrl, CancellationToken ct)
      => Task.FromResult(Result.Failure<ProviderProbeResult>(
          new DomainError("NotWired", "this test never pulls.")));
}
