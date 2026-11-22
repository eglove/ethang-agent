using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain.Tests;

/// <summary>The session preferences carrier, now carrying the VENDOR with each entry: the app
///     owns the model picker and the agent that serves a turn lives in its own host process, so
///     which provider serves a model has to cross that boundary with the model itself.
///     <para>THE TWO LISTS NEVER DISAGREE: assigning ids replaces the entries with vendor-unset
///     ones, and assigning entries replaces the ids with their projection. A caller that names
///     only ids therefore cannot leave a stale vendor behind.</para></summary>
public class SessionModelPreferencesVendorTests
{
  private const string InvalidPreferences = "InvalidModelPreferences";

  [Fact]
  public void AssigningModelIds_LeavesEveryEntrysVendorUnset()
  {
    SessionModelPreferences preferences = new() { ModelIds = ["a/b"] };

    Assert.NotNull(preferences.Entries);
    Assert.Null(preferences.Entries[0].ProviderId);
  }

  [Fact]
  public void AssigningEntries_ProjectsTheIds()
  {
    SessionModelPreferences preferences = new()
    {
      Entries = [new ModelSelectionEntry("a/b", "cloudflare")],
    };

    Assert.Equal(["a/b"], preferences.ModelIds);
  }

  [Fact]
  public void AssigningModelIds_AfterEntries_ClearsTheStaleVendor()
  {
    SessionModelPreferences preferences = new()
    {
      Entries = [new ModelSelectionEntry("a/b", "cloudflare")],
      ModelIds = ["a/b"],
    };

    Assert.NotNull(preferences.Entries);
    Assert.Null(preferences.Entries[0].ProviderId);
  }

  [Fact]
  public void CopyFrom_CopiesTheEntries()
  {
    SessionModelPreferences source = new()
    {
      Entries = [new ModelSelectionEntry("a/b", "cloudflare")],
    };
    SessionModelPreferences target = new();

    target.CopyFrom(source);

    Assert.NotNull(target.Entries);
    Assert.Equal("cloudflare", target.Entries[0].ProviderId);
    Assert.Equal(["a/b"], target.ModelIds);
  }

  [Fact]
  public void RoundTrip_KeepsTheVendor()
  {
    SessionModelPreferences original = new()
    {
      Entries =
      [
        new ModelSelectionEntry("a/b"),
        new ModelSelectionEntry("c/d", "cloudflare"),
      ],
    };

    Result<SessionModelPreferences> restored =
        SessionModelPreferencesJson.Deserialize(SessionModelPreferencesJson.Serialize(original));

    Assert.True(restored.IsSuccess, restored.Error?.Message);
    Assert.Null(restored.Value.Entries![0].ProviderId);
    Assert.Equal("cloudflare", restored.Value.Entries[1].ProviderId);
  }

  [Fact]
  public void Deserialize_RefusesAnUnknownVendorNamingItsPositionAndTheId()
  {
    const string json =
        /*lang=json,strict*/ "{\"Entries\":[{\"ModelId\":\"a/b\",\"ProviderId\":\"gopher\"}]}";

    Result<SessionModelPreferences> restored = SessionModelPreferencesJson.Deserialize(json);

    Assert.False(restored.IsSuccess);
    Assert.Equal(InvalidPreferences, restored.Error.Code);
    Assert.Contains("1", restored.Error.Message, StringComparison.Ordinal);
    Assert.Contains("gopher", restored.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Deserialize_RefusesACarrierCarryingBothShapes()
  {
    // This harness writes exactly one of the two, so a carrier holding both is input no sender
    // produced.
    const string json =
        /*lang=json,strict*/ "{\"ModelIds\":[\"a/b\"],\"Entries\":[{\"ModelId\":\"a/b\"}]}";

    Result<SessionModelPreferences> restored = SessionModelPreferencesJson.Deserialize(json);

    Assert.False(restored.IsSuccess);
    Assert.Equal(InvalidPreferences, restored.Error.Code);
  }

  [Fact]
  public void Deserialize_ACarrierCarryingOnlyIds_StillReads()
  {
    // The shape every carrier written before vendors existed has.
    const string json = /*lang=json,strict*/ "{\"ModelIds\":[\"a/b\",\"c/d\"]}";

    Result<SessionModelPreferences> restored = SessionModelPreferencesJson.Deserialize(json);

    Assert.True(restored.IsSuccess, restored.Error?.Message);
    Assert.Equal(["a/b", "c/d"], restored.Value.ModelIds);
    Assert.All(restored.Value.Entries!, entry => Assert.Null(entry.ProviderId));
  }
}
