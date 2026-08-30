using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.State.Domain.Tests;

/// <summary>state.list must return the key lines themselves — never the C# ToString
///     of the result list (a regression once surfaced as
///     '&lt;&gt;z__ReadOnlyList`1[System.String]' reaching the model).</summary>
public class ListFormattingTests
{
  [Fact]
  public async Task List_ReturnsKeyLines_NotListToString()
  {
    StateCapabilityProvider provider = new(new ListFakeService(["current/head v2", "plans/alpha v1"]));

    CapabilityInvocationResult r = await provider.InvokeAsync("list", "{}", ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsError);
    Assert.DoesNotContain("ReadOnlyList", r.Content, StringComparison.Ordinal);
    Assert.Contains("current/head v2", r.Content, StringComparison.Ordinal);
    Assert.Contains("plans/alpha v1", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_EmptyResult_ReportsExplicitly()
  {
    StateCapabilityProvider provider = new(new ListFakeService([]));

    CapabilityInvocationResult r = await provider.InvokeAsync("list", "{}", ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsError);
    Assert.DoesNotContain("ReadOnlyList", r.Content, StringComparison.Ordinal);
  }

  private sealed class ListFakeService(System.Collections.Generic.IReadOnlyList<string> keys) : IStateService
  {
    public Task<Result<string>> GetAsync(string key, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(Result.Success("v"));

    public Task<Result<StateKeyValue>> SetAsync(string key, string value, int? expectedVersion, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(Result.Success(new StateKeyValue("ns", "name", value, 1)));

    public Task<Result<StateKeyValue>> AppendAsync(string key, string text, int? expectedVersion, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(Result.Success(new StateKeyValue("ns", "name", text, 1)));

    public Task<Result<string>> DeleteAsync(string key, int? expectedVersion, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(Result.Success("deleted"));

    public Task<Result<System.Collections.Generic.IReadOnlyList<string>>> ListAsync(string? ns, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(Result.Success<System.Collections.Generic.IReadOnlyList<string>>(keys));

    public Task<Result<int>> DeletePrefixAsync(string nsPrefix, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(Result.Success(0));

    public Task<Result<System.Collections.Generic.IReadOnlyList<StateSearchHit>>> SearchAsync(string query, int limit, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(Result.Success<System.Collections.Generic.IReadOnlyList<StateSearchHit>>([]));

    public Task<Result<string>> TransitionAsync(string from, string toState, string summary, System.Collections.Generic.IReadOnlyList<string> evidence, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(Result.Success("tr-1"));

    public Task<CertificationReport> VerifyAsync(System.Collections.Generic.IReadOnlyList<string>? ids, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(new CertificationReport(true, false, [], []));

    public Task<CertificationReport> CheckGoalAsync(System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(new CertificationReport(true, false, [], []));

    public Task<Result<System.Collections.Generic.IReadOnlyList<string>>> HistoryAsync(int limit, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(Result.Success<System.Collections.Generic.IReadOnlyList<string>>([]));
  }
}
