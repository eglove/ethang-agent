using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>IWebAccess fake returning a canned result and recording the last URL.</summary>
internal sealed class FakeWebAccess(Result<WebResource> response) : IWebAccess
{
  public Uri? LastUrl { get; private set; }

  public Task<Result<WebResource>> FetchAsync(Uri url, CancellationToken ct = default)
  {
    LastUrl = url;
    return Task.FromResult(response);
  }
}
