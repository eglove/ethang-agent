using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Model.Domain.Tests;

/// <summary>The session preferences carrier: the app owns the model picker and the agent that
///     serves a turn lives in its own host process, so the ORDERED MODEL LIST has to cross that
///     boundary or the host falls back to the persisted pin and serves a model the user did not
///     put first.
///     <para>The carrier is opaque JSON: the transport never interprets it, and a carrier that
///     cannot be read is a typed failure rather than an exception crossing the wire.</para>
///     <para>AN ABSENT LIST AND A BLANK ENTRY ARE DIFFERENT STATEMENTS. Absent is a sender that
///     stated no preference (an older carrier, or a session that never picked a model), so the
///     host resolves as it always did. A blank entry is a list that cannot be rendered as the
///     request's ordered <c>models</c> array, so it is refused by name instead of compacted.
///     </para></summary>
public class SessionModelPreferencesJsonTests
{
  [Fact]
  public void RoundTrip_AnOrderedList_KeepsOrderVariantAndRetention()
  {
    SessionModelPreferences original = new()
    {
      ModelIds = ["alpha/model", "beta/other", "gamma/third"],
      ModelVariant = ModelVariant.Nitro,
      Retention = RetentionPolicy.SafetyRetention,
    };

    Result<SessionModelPreferences> restored =
        SessionModelPreferencesJson.Deserialize(SessionModelPreferencesJson.Serialize(original));

    Assert.True(restored.IsSuccess, restored.Error?.Message);
    Assert.Equal(["alpha/model", "beta/other", "gamma/third"], restored.Value.ModelIds);
    Assert.Equal(ModelVariant.Nitro, restored.Value.ModelVariant);
    Assert.Equal(RetentionPolicy.SafetyRetention, restored.Value.Retention);
  }

  [Fact]
  public void RoundTrip_AReorderedList_ComesBackInTheOrderItWasSent()
  {
    // Order is part of the value: the provider falls through in this sequence, so a carrier
    // that returned the same ids in another order would serve a different model than the one
    // the user put first.
    SessionModelPreferences original = new() { ModelIds = ["beta/other", "alpha/model"] };

    Result<SessionModelPreferences> restored =
        SessionModelPreferencesJson.Deserialize(SessionModelPreferencesJson.Serialize(original));

    Assert.True(restored.IsSuccess, restored.Error?.Message);
    Assert.Equal(["beta/other", "alpha/model"], restored.Value.ModelIds);
  }

  [Fact]
  public void Deserialize_ADocumentWithNoListMember_YieldsANullList()
  {
    // The shape an older carrier writes, and a carrier whose sender stated no model at all:
    // the host must resolve from its own pin exactly as it does today, so an absent list is
    // readable rather than refused.
    const string json = /*lang=json,strict*/ "{\"ModelVariant\":1}";

    Result<SessionModelPreferences> restored = SessionModelPreferencesJson.Deserialize(json);

    Assert.True(restored.IsSuccess, restored.Error?.Message);
    Assert.Null(restored.Value.ModelIds);
    Assert.Equal(ModelVariant.Nitro, restored.Value.ModelVariant);
  }

  // JSON002 fires only in the format/IDE host, which cannot see that these literals are carriers
  // under test - the pragma pair is the repo's named decision for hand-written JSON test shapes.
#pragma warning disable JSON002 // Probable JSON string detected
  [Theory]
  [InlineData("{\"ModelIds\":[\"alpha/model\",\"\"]}")]
  [InlineData("{\"ModelIds\":[\"alpha/model\",\"   \"]}")]
  [InlineData("{\"ModelIds\":[\"alpha/model\",null]}")]
  public void Deserialize_AListEntryNamingNoModel_FailsInvalidModelPreferences(string json)
  {
    // Compacting the blank row away would put a list on the wire that the carrier never
    // stated, so the row is refused by position and the turn says why.
    Result<SessionModelPreferences> restored = SessionModelPreferencesJson.Deserialize(json);

    Assert.False(restored.IsSuccess);
    Assert.Equal("InvalidModelPreferences", restored.Error.Code);
    Assert.Contains("position 2", restored.Error.Message, StringComparison.Ordinal);
  }
#pragma warning restore JSON002 // Probable JSON string detected

  [Fact]
  public void Deserialize_ABlankHead_FailsNamingPositionOne()
  {
    // The head is the model that runs; a blank first entry is the one position that cannot be
    // skipped over to reach a valid backup.
    const string json = /*lang=json,strict*/ "{\"ModelIds\":[\"\",\"beta/other\"]}";

    Result<SessionModelPreferences> restored = SessionModelPreferencesJson.Deserialize(json);

    Assert.False(restored.IsSuccess);
    Assert.Equal("InvalidModelPreferences", restored.Error.Code);
    Assert.Contains("position 1", restored.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Deserialize_ABlankEntry_DoesNotSilentlyDropTheEntriesAfterIt()
  {
    const string json =
        /*lang=json,strict*/ "{\"ModelIds\":[\"alpha/model\",\"\",\"gamma/third\"]}";

    Result<SessionModelPreferences> restored = SessionModelPreferencesJson.Deserialize(json);

    Assert.False(restored.IsSuccess);
    Assert.Equal("InvalidModelPreferences", restored.Error.Code);
    Assert.Contains("position 2", restored.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Deserialize_AnEmptyList_IsReadableAsNoSelection()
  {
    // Nothing to fall through to: consumers read a list of no entries as no live preference,
    // which is the pin's path, not a list of zero models to send.
    const string json = /*lang=json,strict*/ "{\"ModelIds\":[]}";

    Result<SessionModelPreferences> restored = SessionModelPreferencesJson.Deserialize(json);

    Assert.True(restored.IsSuccess, restored.Error?.Message);
    Assert.Empty(restored.Value.ModelIds ?? []);
  }
}
