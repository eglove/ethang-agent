using System.Net;
using eThangAgent.ToolDomain;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The management client's key must be READ LIVE at dispatch: a key saved
///     mid-session (Settings, API Keys) lands on the carrier and the next call uses
///     it — the snapshot-config behavior forced a session restart to pick a key up
///     (2026-10-08). Absent carrier key still refuses with ManagementUnavailable.</summary>
public class OpenRouterManagementLiveKeyTests
{
  private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
      new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

  private const string CreditsJson =
      /*lang=json,strict*/ "{\"data\":{\"total_credits\":10,\"total_usage\":2}}";

  [Fact]
  public async Task KeySavedAfterConstruction_IsUsed_OnTheNextDispatch()
  {
    List<HttpRequestMessage> seen = [];
    FakeHttpMessageHandler handler = new(req =>
    {
      seen.Add(req);
      return Task.FromResult(Json(HttpStatusCode.OK, CreditsJson));
    });
    OpenRouterManagementKeyCarrier carrier = new();
    // The snapshot config carries NO management key — the pre-fix world where a
    // key saved later was invisible.
    OpenRouterManagementClient client = new(new HttpClient(handler),
        new OpenRouterConfiguration("sk-or-model", new Uri("https://openrouter.test")),
        managementKeySource: () => carrier.Current);

    // 1. No key yet: the typed refusal.
    OpenRouterManagementOutcome before = await client.ExecuteAsync(
        new OpenRouterManagementCommand.GetCredits(), TestContext.Current.CancellationToken);
    OpenRouterManagementOutcome.Failure failure = Assert.IsType<OpenRouterManagementOutcome.Failure>(before);
    Assert.Equal("ManagementUnavailable", failure.Code);

    // 2. The user saves a key mid-session: the carrier picks it up live.
    carrier.Current = "sk-or-mng-live";
    OpenRouterManagementOutcome after = await client.ExecuteAsync(
        new OpenRouterManagementCommand.GetCredits(), TestContext.Current.CancellationToken);
    _ = Assert.IsType<OpenRouterManagementOutcome.Credits>(after);
    HttpRequestMessage sent = Assert.Single(seen);
    Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);
    Assert.Equal("sk-or-mng-live", sent.Headers.Authorization.Parameter);
  }

  [Fact]
  public async Task SnapshotConfigKey_StillWorks_WithoutACarrier()
  {
    List<HttpRequestMessage> seen = [];
    FakeHttpMessageHandler handler = new(req =>
    {
      seen.Add(req);
      return Task.FromResult(Json(HttpStatusCode.OK, CreditsJson));
    });
    OpenRouterManagementClient client = new(new HttpClient(handler),
        new OpenRouterConfiguration("sk-or-model", new Uri("https://openrouter.test"), ManagementKey: "sk-or-mng-snapshot"));

    OpenRouterManagementOutcome outcome = await client.ExecuteAsync(
        new OpenRouterManagementCommand.GetCredits(), TestContext.Current.CancellationToken);
    _ = Assert.IsType<OpenRouterManagementOutcome.Credits>(outcome);
    HttpRequestMessage sent = Assert.Single(seen);
    Assert.Equal("sk-or-mng-snapshot", sent.Headers.Authorization!.Parameter);
  }
}
