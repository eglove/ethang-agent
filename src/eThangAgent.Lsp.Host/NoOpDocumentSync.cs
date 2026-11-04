using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.Host;

public sealed class NoOpDocumentSync : ILspDocumentSync
{
  public Task DidOpenAsync(string uri, string languageId, int version, string text, CancellationToken ct = default) => Task.CompletedTask;
  public Task DidChangeAsync(string uri, int version, string text, CancellationToken ct = default) => Task.CompletedTask;
  public Task DidCloseAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;
}
