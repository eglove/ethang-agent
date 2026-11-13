using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

/// <summary>The ordered model list over the WHOLE path: the session's live preference becomes
///     the turn's model and its fallbacks, the OpenRouter ACL puts the list on the wire, and what
///     the provider reports back is what the session records as served.
///     <para>ONE COMPOSED RUN, NOT TWO MOCKED HALVES. The subject is that the preference, the
///     resolver, the overlay, and the request body agree about which model comes first. A test
///     that built the config itself would prove the ACL can send a list while saying nothing
///     about whether a session ever produces one.</para>
///     <para>THE STATUS BAR IS NOT ASSERTED HERE, because the surface that renders it lives in
///     the Desktop assembly. What this file proves is the FACT the bar consumes - the recorded
///     usage row names the model the provider reported - and the bar's own rule over that row
///     (head answered, backup answered) is pinned in the Desktop suite's StatusModelTests.</para>
///     <para>NOTHING HERE ASSERTS A DURATION: every wait is a ceiling, never a latency
///     contract.</para></summary>
[Trait("Category", "Process")]
public class ModelListSessionTests
{
  private const string Head = "alpha/model";
  private const string Second = "beta/other";

  // 1: the list reaches the wire, head first.

  [Fact]
  public async Task ATwoEntryPreference_SendsBothIdsInOrder_WithTheHeadAsTheModel()
  {
    using ComposedHostHarness session = await ComposedHostHarness
        .StartAsync([Head, Second]);

    Result<string> turn = await session.SendAsync("hi").ConfigureAwait(true);

    Assert.True(turn.IsSuccess, turn.Error?.Message);
    using JsonDocument request = JsonDocument.Parse(Assert.Single(session.Provider.ResponseBodies));
    Assert.Equal([Head, Second], ModelsOf(request.RootElement));
    Assert.Equal(Head, request.RootElement.GetProperty("model").GetString());
  }

  // 2: the primary serves, and the record says so.

  [Fact]
  public async Task ATurnThePrimaryAnswered_RecordsThePrimaryAsServed()
  {
    using ComposedHostHarness session = await ComposedHostHarness
        .StartAsync([Head, Second]);
    session.Provider.ServedModelId = Head;

    Result<string> turn = await session.SendAsync("hi").ConfigureAwait(true);

    Assert.True(turn.IsSuccess, turn.Error?.Message);
    UsageRow recorded = Assert.Single(session.Usage);
    Assert.Equal(Head, recorded.ServedModelId);
    Assert.Equal(Head, recorded.ModelId);
    Assert.Equal(Head, recorded.RequestedModelId);
  }

  // 3: a backup serves, and the record follows the provider.

  [Fact]
  public async Task ATurnTheBackupAnswered_RecordsTheBackupAsServed()
  {
    // The provider fell through to the second entry and said so. The session REQUESTED the
    // head - that is what its request carried - and the row reports what actually answered.
    using ComposedHostHarness session = await ComposedHostHarness
        .StartAsync([Head, Second]);
    session.Provider.ServedModelId = Second;

    Result<string> turn = await session.SendAsync("hi").ConfigureAwait(true);

    Assert.True(turn.IsSuccess, turn.Error?.Message);
    UsageRow recorded = Assert.Single(session.Usage);
    Assert.Equal(Second, recorded.ServedModelId);
    Assert.Equal(Head, recorded.RequestedModelId);
    Assert.Equal(Second, recorded.ModelId);
    // The LIST is what the user stated: an answering backup is never promoted into it.
    Assert.Equal([Head, Second], session.Preference.ModelIds!);
  }

  // 4: a failover does not move the head.

  [Fact]
  public async Task AfterAFailover_TheNextTurnStillSendsTheHeadFirst()
  {
    using ComposedHostHarness session = await ComposedHostHarness
        .StartAsync([Head, Second]);
    session.Provider.ServedModelId = Second;

    Result<string> first = await session.SendAsync("first").ConfigureAwait(true);
    Result<string> second = await session.SendAsync("second").ConfigureAwait(true);

    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.True(second.IsSuccess, second.Error?.Message);
    Assert.Equal(Second, session.Usage[0].ServedModelId);
    IReadOnlyList<string> bodies = session.Provider.ResponseBodies;
    Assert.Equal(2, bodies.Count);
    foreach (string body in bodies)
    {
      using JsonDocument request = JsonDocument.Parse(body);
      Assert.Equal([Head, Second], ModelsOf(request.RootElement));
      Assert.Equal(Head, request.RootElement.GetProperty("model").GetString());
    }
  }

  // 5: the whole list fails.

  [Fact]
  public async Task WhenTheProviderRefusesTheWholeList_TheTurnFailsWithItsErrorAndTheTranscriptKeepsIt()
  {
    const string Refusal = "no endpoints available for any listed model";
    using ComposedHostHarness session = await ComposedHostHarness
        .StartAsync([Head, Second]);
    session.Provider.Responder = _ => MockAnswer.Refuse(400,
        JsonSerializer.Serialize(new { error = new { message = Refusal } }));

    Result<string> turn = await session.SendAsync("hi").ConfigureAwait(true);

    Assert.False(turn.IsSuccess, "a refused list must fail the turn, never serve another model");
    Assert.Equal("ProviderError", turn.Error?.Code);
    Assert.Contains(Refusal, turn.Error!.Message, StringComparison.Ordinal);
    // The transcript keeps the provider's own words: a turn that dies on one must say why.
    Assert.Contains(session.Conversation.Messages,
        message => message.Content.Contains(Refusal, StringComparison.Ordinal));
    // NO SESSION FALLBACK: every request the provider saw asked for a model the user listed.
    Assert.All(session.Provider.ResponseBodies, body =>
    {
      using JsonDocument request = JsonDocument.Parse(body);
      Assert.Contains(request.RootElement.GetProperty("model").GetString(), new[] { Head, Second });
    });
  }

  // 6: an over-long list is the provider's refusal, and the list stands.

  [Fact]
  public async Task AListTheProviderRefuses_TypedErrorNamesTheRefusal_AndTheStoredListIsUntouched()
  {
    const int ProviderLimit = 20;
    string[] overLong =
    [
      Head,
      Second,
      .. Enumerable.Range(0, ProviderLimit).Select(index => $"many/model{index}"),
    ];
    using ComposedHostHarness session = await ComposedHostHarness
        .StartAsync(overLong);
    string storedBefore = session.StoredValue;
    session.Provider.Responder = body => RefuseLongLists(body, ProviderLimit);

    Result<string> turn = await session.SendAsync("hi").ConfigureAwait(true);

    Assert.False(turn.IsSuccess);
    Assert.Equal("ProviderError", turn.Error?.Code);
    Assert.Contains($"models accepts at most {ProviderLimit}", turn.Error!.Message,
        StringComparison.Ordinal);
    // THE FAILURE TRIMS NOTHING: neither the stored preference nor the live selection was
    // reordered or shortened by a refusal at request time.
    Assert.Equal(storedBefore, await session.ReadStoredValueAsync().ConfigureAwait(true));
    Assert.Equal(overLong, session.Preference.ModelIds!);
  }

  // 8: an untouched workspace's traffic is unchanged.

  [Theory]
  // The two shapes that predate the list: a bare model id, and the single-choice document.
  // Both restore as a ONE-entry selection, which is what keeps an untouched workspace's request
  // byte-identical to the one it sent before the list existed.
  [InlineData("alpha/model", "Standard")]
  [InlineData(
                           /*lang=json,strict*/
                           """{"ModelId":"alpha/model","Variant":"Nitro","Retention":"SafetyRetention"}""",
      "Nitro")]
  public async Task AWorkspaceRestoredFromAnOlderStoredValue_SendsNoModelsKeyAtAll(
      string stored, string expectedVariant)
  {
    ModelSelection restored = ModelSelection.Parse(stored).Value!;
    Assert.Equal([Head], restored.ModelIds);
    Assert.Equal(expectedVariant, restored.Variant.ToString());
    using ComposedHostHarness session = await ComposedHostHarness
        .StartAsync(restored.ModelIds);
    session.Preference.ModelVariant = restored.Variant;

    Result<string> turn = await session.SendAsync("hi").ConfigureAwait(true);

    Assert.True(turn.IsSuccess, turn.Error?.Message);
    using JsonDocument request = JsonDocument.Parse(Assert.Single(session.Provider.ResponseBodies));
    Assert.False(request.RootElement.TryGetProperty("models", out _),
        "a one-entry selection must send the body this harness has always sent");
    // And the request still carries the model that workspace had chosen, under its own variant.
    Assert.Equal(restored.WireModelIds[0],
        request.RootElement.GetProperty("model").GetString());
  }

  /// <summary>The provider's own refusal of a list it will not accept: a 400 whose body names
  ///      the limit and the member that broke it, exactly as a real provider would.</summary>
  private static MockAnswer RefuseLongLists(string body, int limit)
  {
    using JsonDocument request = JsonDocument.Parse(body);
    int entries = request.RootElement.GetProperty("models").GetArrayLength();
    return entries > limit
        ? MockAnswer.Refuse(400, JsonSerializer.Serialize(new
        {
          error = new
          {
            message = $"models accepts at most {limit} entries; the request listed {entries}",
            type = "invalid_request_error",
          },
        }))
        : MockAnswer.Ok(MockProviderServer.Completion(Head));
  }

  /// <summary>The ids a request body carries in <c>models</c>, in order.</summary>
  private static IReadOnlyList<string> ModelsOf(JsonElement root)
      => [.. root.GetProperty("models").EnumerateArray().Select(entry => entry.GetString()!)];
}
