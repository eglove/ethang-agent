using System.Text;

namespace eThangAgent.Desktop.Tests;

/// <summary>The mock serves requests CONCURRENTLY: a delayed in-flight response must
///     never block the loop from receiving and serving the next request — the steer-
///     during-wait E2E depends on the parent's wait request being served while the
///     child's delayed response is still pending. The sequential-loop regression made
///     the child's delay also delay the parent's requests, racing the wait's subscribe
///     against the child's settlement.</summary>
public class MockConcurrencyTests
{
  [Fact]
  public async Task DelayedResponse_DoesNotBlockTheNextRequest()
  {
    string slowUrl;
    string fastUrl;
    string fastBody;
    string slowBody;
    using (MockOpenRouterServer mock = new())
    {
      mock.Start();
      _ = mock.DelayResponsesForModel("slow/model", TimeSpan.FromSeconds(3));
      _ = mock.ReturnsForModel("slow/model", E2E.RawCompletion("slow reply"));
      _ = mock.ReturnsForModel("fast/model", E2E.RawCompletion("fast reply"));
      slowUrl = new Uri(mock.BaseUrl, "api/v1/responses").ToString();
      fastUrl = slowUrl;

      // Fire the slow request, then — while its response is still delayed — the fast one.
      Task<string> slow = PostResponsesAsync(slowUrl, E2E.RawCompletionBody("slow/model"));
      await Task.Delay(200, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Task<string> fast = PostResponsesAsync(fastUrl, E2E.RawCompletionBody("fast/model"));

      // The fast request must complete WELL before the slow one's delay elapses.
      _ = await fast.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
      fastBody = await fast.ConfigureAwait(true);
      slowBody = await slow.ConfigureAwait(true);
    }

    Assert.Contains("fast reply", fastBody, StringComparison.Ordinal);
    Assert.Contains("slow reply", slowBody, StringComparison.Ordinal);
  }

  private static async Task<string> PostResponsesAsync(string url, string body)
  {
    using HttpClient client = new();
    Uri uri = new(url);
    using StringContent content = new(body, Encoding.UTF8, "application/json");
    using HttpResponseMessage response = await client.PostAsync(uri, content, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
  }
}
