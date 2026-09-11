using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

/// <summary>Provider identity for the local OpenAI-compatible provider, the
///     settings shape hosts overlay credentials onto, and the overlay behavior.
///     The spec pins each name verbatim — tests are the pin.</summary>
[Collection("EnvironmentSensitive")]
public class ProvidersAndLocalSettingsTests
{
  // --- Provider identity (Providers) ---

  [Fact]
  public void IsKnown_Local_True()
    => Assert.True(Providers.IsKnown(Providers.Local));

  [Fact]
  public void DisplayName_Local_MatchesCopy()
    => Assert.Equal("Local (OpenAI-compatible)", Providers.DisplayName(Providers.Local));

  [Fact]
  public void FallbackModelId_Local_Throws()
    // The fail-loudly pin: the local fallback travels a different path (the session
    // default, never this method) — a caller landing here is a threading bug.
    => Assert.Throws<ArgumentOutOfRangeException>(() => Providers.FallbackModelId(Providers.Local));

  // --- LocalSettings ---

  [Fact]
  public void Preference_Keys_Name_The_Stored_Slots()
  {
    Assert.Equal("local_api_key", LocalSettings.PreferenceKey);
    Assert.Equal("local_base_url", LocalSettings.BaseUrlPreferenceKey);
  }

  [Fact]
  public void HasText_Blank_False()
    => Assert.False(new LocalSettings(null, null).HasText
        && new LocalSettings("", null).HasText
        && new LocalSettings("   ", null).HasText);

  [Fact]
  public void HasText_TextSet_True()
    => Assert.True(new LocalSettings("http://localhost:8080/v1", null).HasText);

  [Fact]
  public void ResolveBaseUrl_Valid_Parses()
  {
    Result<Uri> result = new LocalSettings("http://localhost:8080/v1", null).ResolveBaseUrl();

    Assert.True(result.IsSuccess);
    Assert.Equal(new Uri("http://localhost:8080/v1"), result.Value);
  }

  [Fact]
  public void ResolveBaseUrl_InvalidText_NamedFailure()
  {
    Result<Uri> result = new LocalSettings("not-a-url", null).ResolveBaseUrl();

    Assert.False(result.IsSuccess);
    Assert.NotNull(result.Error);
    Assert.Equal("InvalidLocalBaseUrl", result.Error.Code);
  }

  [Fact]
  public void ResolveBaseUrl_Blank_NamedFailure()
  {
    // Callers check HasText first; blank text still fails with the same named code,
    // never a silent default.
    Result<Uri> result = new LocalSettings("   ", null).ResolveBaseUrl();

    Assert.False(result.IsSuccess);
    Assert.NotNull(result.Error);
    Assert.Equal("InvalidLocalBaseUrl", result.Error.Code);
  }

  // --- AgentSettings overlays ---

  [Fact]
  public void HasLocal_NullSettings_False()
    => Assert.False(Settings().HasLocal);

  [Fact]
  public void HasLocal_BlankText_False()
    => Assert.False(Settings(localBaseUrl: "   ").HasLocal);

  [Fact]
  public void HasLocal_TextSet_True()
    => Assert.True(Settings(localBaseUrl: "http://localhost:8080/v1").HasLocal);

  [Fact]
  public void WithLocalSettings_OverlaysBoth()
  {
    AgentSettings overlaid = Settings().WithLocalSettings("http://localhost:8080/v1", "local-key");

    Assert.NotNull(overlaid.Local);
    Assert.Equal("http://localhost:8080/v1", overlaid.Local.BaseUrlText);
    Assert.Equal("local-key", overlaid.Local.ApiKey);
    // Untouched members carry over.
    Assert.True(overlaid.HasOpenRouter);
    Assert.True(overlaid.HasZai);
  }

  [Fact]
  public void WithLocalSettings_Null_Clears()
  {
    AgentSettings overlaid = Settings(localBaseUrl: "http://localhost:8080/v1")
        .WithLocalSettings(null, null);

    Assert.NotNull(overlaid.Local);
    Assert.False(overlaid.Local.HasText);
    Assert.False(overlaid.HasLocal);
  }

  [Fact]
  public void WithApiKeys_LocalOverlay()
  {
    AgentSettings overlaid = Settings().WithApiKeys("sk-or-test", "zai-key", "local-key");

    Assert.Equal("sk-or-test", overlaid.OpenRouter.ApiKey);
    Assert.Equal("zai-key", overlaid.Zai.ApiKey);
    Assert.NotNull(overlaid.Local);
    Assert.Equal("local-key", overlaid.Local.ApiKey);
  }

  [Fact]
  public void WithApiKeys_TwoArguments_Still_Compiles_And_Leaves_LocalKey_Null()
  {
    // Existing construction/overlay sites keep compiling: the local key is an
    // optional third overlay, null by default.
    AgentSettings overlaid = Settings().WithApiKeys("sk-or-test", "zai-key");

    Assert.NotNull(overlaid.Local);
    Assert.Null(overlaid.Local.ApiKey);
  }

  private static AgentSettings Settings(string? localBaseUrl = null) => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new ZaiSettings("zai-key", new Uri("https://zai.test")),
      new SubAgentOptions(null, 2),
      Local: localBaseUrl is null ? null : new LocalSettings(localBaseUrl, "local-key"));
}
