using eThangAgent.AgentDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>The Cloudflare credential is APP-WIDE and lives on the settings record exactly as
///     the OpenRouter key does: its own protected preference keys, a null-means-absent
///     credential, and a blank member that states 'not configured' rather than 'absent'.
///     <para>A MISSING CREDENTIAL IS A CONFIGURATION FACT, not a provider failure: it is
///     readable from <see cref="AgentSettings.HasCloudflare"/> without a call, which is what
///     lets every surface hide the provider instead of offering one that cannot answer.</para>
///     </summary>
public class CloudflareSettingsTests
{
  private static AgentSettings Settings(string? openRouterKey = "sk-or-test",
      CloudflareSettings? cloudflare = null) => new(
          new OpenRouterSettings(openRouterKey, new Uri("https://openrouter.test")),
          new AgentRunOptions(null, 2),
          Cloudflare: cloudflare);

  [Fact]
  public void PreferenceKeys_AreTheCloudflareOnes()
  {
    Assert.Equal("cloudflare_account_id", CloudflareSettings.AccountIdPreferenceKey);
    Assert.Equal("cloudflare_api_token", CloudflareSettings.ApiTokenPreferenceKey);
    Assert.Equal("https://api.cloudflare.com", CloudflareSettings.DefaultBaseUrl);
  }

  [Fact]
  public void ApiBaseUrl_CarriesTheVersionedPrefixTheLiveApiIsServedUnder() =>
      Assert.Equal("https://api.cloudflare.com/client/v4", CloudflareSettings.ApiBaseUrl);

  [Fact]
  public void TheCredentialKeys_AreNotTheNonSecretPreferenceKeys()
  {
    // AgentPreferenceKeys holds NON-SECRET settings only; a secret keeps its own key on the
    // settings record, which is the OpenRouterSettings.PreferenceKey precedent.
    string[] secretKeys =
    [
        CloudflareSettings.AccountIdPreferenceKey,
        CloudflareSettings.ApiTokenPreferenceKey,
        OpenRouterSettings.PreferenceKey,
        OpenRouterSettings.ManagementKeyPreferenceKey,
    ];
    Assert.All(secretKeys, key => Assert.DoesNotContain(key, NonSecretKeys(), StringComparer.Ordinal));
  }

  [Fact]
  public void HasCloudflare_IsFalse_WhenNoCredentialWasSupplied()
  {
    Assert.False(Settings().HasCloudflare);
    Assert.Null(Settings().Cloudflare);
  }

  [Theory]
  [InlineData(null, null)]
  [InlineData("acct", null)]
  [InlineData(null, "token")]
  [InlineData("", "token")]
  [InlineData("acct", "")]
  [InlineData("   ", "token")]
  [InlineData("acct", "   ")]
  public void HasCloudflare_IsFalse_WhenEitherMemberIsBlank(string? accountId, string? apiToken)
  {
    AgentSettings settings = Settings(cloudflare: new CloudflareSettings(accountId, apiToken));

    Assert.False(settings.HasCloudflare);
  }

  [Fact]
  public void HasCloudflare_IsTrue_WhenBothMembersArePresent()
  {
    AgentSettings settings = Settings(cloudflare: new CloudflareSettings("acct", "token"));

    Assert.True(settings.HasCloudflare);
  }

  [Fact]
  public void WithCloudflareCredential_OverlaysBothMembers()
  {
    AgentSettings overlaid = Settings().WithCloudflareCredential("acct", "token");

    Assert.True(overlaid.HasCloudflare);
    Assert.Equal("acct", overlaid.Cloudflare!.AccountId);
    Assert.Equal("token", overlaid.Cloudflare.ApiToken);
  }

  [Fact]
  public void WithCloudflareCredential_NullKeepsWhatWasAlreadySet()
  {
    AgentSettings overlaid = Settings(cloudflare: new CloudflareSettings("acct", "token"))
        .WithCloudflareCredential(null, null);

    Assert.Equal("acct", overlaid.Cloudflare!.AccountId);
    Assert.Equal("token", overlaid.Cloudflare.ApiToken);
  }

  [Fact]
  public void WithCloudflareCredential_OverlaysOneMemberAndKeepsTheOther()
  {
    AgentSettings overlaid = Settings(cloudflare: new CloudflareSettings("acct", null))
        .WithCloudflareCredential(null, "token");

    Assert.Equal("acct", overlaid.Cloudflare!.AccountId);
    Assert.Equal("token", overlaid.Cloudflare.ApiToken);
  }

  [Fact]
  public void WithCloudflareCredential_DoesNotMutateTheOriginal()
  {
    AgentSettings original = Settings(cloudflare: new CloudflareSettings("acct", "token"));

    _ = original.WithCloudflareCredential("other", "other");

    Assert.Equal("acct", original.Cloudflare!.AccountId);
    Assert.Equal("token", original.Cloudflare.ApiToken);
  }

  [Fact]
  public void WithCloudflareCredential_DoesNotDisturbTheOpenRouterCredential()
  {
    AgentSettings overlaid = Settings(openRouterKey: "sk-or-test").WithCloudflareCredential("acct", "token");

    Assert.True(overlaid.HasOpenRouter);
    Assert.Equal("sk-or-test", overlaid.OpenRouter.ApiKey);
    Assert.Equal("https://openrouter.test/", overlaid.OpenRouter.BaseUrl.ToString());
  }

  [Fact]
  public async Task LoadedSettings_CarryBothProvidersCredentials()
  {
    FakePreferenceStore preferences = new FakePreferenceStore()
        .With(CloudflareSettings.AccountIdPreferenceKey, "acct-1")
        .With(CloudflareSettings.ApiTokenPreferenceKey, "token-1");

    AgentSettings loaded = await AgentSettingsLoader.LoadAsync(preferences);

    Assert.Equal("acct-1", loaded.Cloudflare!.AccountId);
    Assert.Equal("token-1", loaded.Cloudflare.ApiToken);
    Assert.True(loaded.HasCloudflare);
  }

  [Fact]
  public async Task LoadedSettings_WithNeitherCredential_StillBuild()
  {
    AgentSettings loaded = await AgentSettingsLoader.LoadAsync(new FakePreferenceStore());

    Assert.Null(loaded.Cloudflare);
    Assert.False(loaded.HasCloudflare);
    Assert.False(loaded.HasOpenRouter);
  }

  [Fact]
  public async Task LoadedSettings_WithOnlyOneCloudflareMember_CarryItAndReportNoCredential()
  {
    FakePreferenceStore preferences = new FakePreferenceStore()
        .With(CloudflareSettings.AccountIdPreferenceKey, "acct-1");

    AgentSettings loaded = await AgentSettingsLoader.LoadAsync(preferences);

    Assert.Equal("acct-1", loaded.Cloudflare!.AccountId);
    Assert.Null(loaded.Cloudflare.ApiToken);
    Assert.False(loaded.HasCloudflare);
  }

  [Fact]
  public async Task LoadedSettings_RoundTripTheCredentialUnchanged()
  {
    FakePreferenceStore preferences = new FakePreferenceStore()
        .With(CloudflareSettings.AccountIdPreferenceKey, "acct-1")
        .With(CloudflareSettings.ApiTokenPreferenceKey, "token-1");

    AgentSettings first = await AgentSettingsLoader.LoadAsync(preferences);
    AgentSettings second = await AgentSettingsLoader.LoadAsync(preferences);

    Assert.Equal(first.Cloudflare, second.Cloudflare);
    Assert.Equal(new CloudflareSettings("acct-1", "token-1"), second.Cloudflare);
  }

  [Fact]
  public async Task ClearingTheStoredCredential_RemovesItOnTheNextLoad()
  {
    FakePreferenceStore preferences = new FakePreferenceStore()
        .With(CloudflareSettings.AccountIdPreferenceKey, "acct-1")
        .With(CloudflareSettings.ApiTokenPreferenceKey, "token-1");
    Assert.True((await AgentSettingsLoader.LoadAsync(preferences)).HasCloudflare);

    _ = await preferences.DeleteAsync(CloudflareSettings.AccountIdPreferenceKey,
        TestContext.Current.CancellationToken);
    _ = await preferences.DeleteAsync(CloudflareSettings.ApiTokenPreferenceKey,
        TestContext.Current.CancellationToken);

    AgentSettings cleared = await AgentSettingsLoader.LoadAsync(preferences);
    Assert.Null(cleared.Cloudflare);
    Assert.False(cleared.HasCloudflare);
  }

  [Fact]
  public async Task LoadingTheCredential_PersistsNothing()
  {
    // The database is the only artifact: a credential is a preference row, never a file, and
    // reading it is a read. A load that wrote anything would be a second durable copy.
    FakePreferenceStore preferences = new FakePreferenceStore()
        .With(CloudflareSettings.AccountIdPreferenceKey, "acct-1")
        .With(CloudflareSettings.ApiTokenPreferenceKey, "token-1");

    AgentSettings loaded = await AgentSettingsLoader.LoadAsync(preferences);

    Assert.True(loaded.HasCloudflare);
    Assert.Empty(preferences.Writes);
  }

  /// <summary>Every non-secret preference key this build knows, read from the one type that
  ///     holds them. Named as a list so the assertion above is about the KEYS, not about the
  ///     type's membership.</summary>
  private static string[] NonSecretKeys() =>
  [
      AgentPreferenceKeys.MaxConcurrentAgents,
      AgentPreferenceKeys.DefaultModel,
      AgentPreferenceKeys.ComputerUseEnabled,
      AgentPreferenceKeys.VerificationGateEnabled,
      AgentPreferenceKeys.SkillRegistryDefaultTarget,
      AgentPreferenceKeys.WatchdogTickInterval,
      AgentPreferenceKeys.WatchdogIdleThreshold,
      AgentPreferenceKeys.WatchdogMaxWrapUpAttempts,
      AgentPreferenceKeys.RootTurnIdle,
      AgentPreferenceKeys.OpenRouterBaseUrl,
  ];

}
