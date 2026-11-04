using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL;

/// <summary>Production <see cref="ILspSymbolAccess"/> over the symbol service
/// (issue #357): translates domain commands to ACL queries and maps honesty
/// labels back. The service is resolved lazily so the tool can be registered
/// before any language server exists; a null service yields Unavailable.</summary>
public sealed class LspSymbolAccess : ILspSymbolAccess
{
  private readonly Func<LspSymbolService?> _serviceFactory;

  public LspSymbolAccess(Func<LspSymbolService?> serviceFactory)
    => _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));

  public async Task<LspSymbolOutcome> ExecuteAsync(LspSymbolCommand command, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    LspSymbolService? service = _serviceFactory();
    if (service is null)
    {
      return new LspSymbolOutcome.Unavailable("no language server for this workspace");
    }

    LspSymbolResult result = command switch
    {
      LspSymbolCommand.Definition c => await service.DefinitionAsync(ToUri(c.Path), c.SymbolName, c.Occurrence, ct).ConfigureAwait(false),
      LspSymbolCommand.References c => await service.ReferencesAsync(ToUri(c.Path), c.SymbolName, c.Occurrence, ct).ConfigureAwait(false),
      LspSymbolCommand.Hover c => await service.HoverAsync(ToUri(c.Path), c.SymbolName, c.Occurrence, ct).ConfigureAwait(false),
      LspSymbolCommand.DocumentSymbols c => await service.DocumentSymbolsAsync(ToUri(c.Path), ct).ConfigureAwait(false),
      LspSymbolCommand.WorkspaceSymbols c => await service.WorkspaceSymbolsAsync(c.SymbolName, ct).ConfigureAwait(false),
      LspSymbolCommand.Implementation c => await service.ImplementationAsync(ToUri(c.Path), c.SymbolName, c.Occurrence, ct).ConfigureAwait(false),
      LspSymbolCommand.CallHierarchyIncoming c => await service.CallHierarchyIncomingAsync(ToUri(c.Path), c.SymbolName, c.Occurrence, c.Depth, ct).ConfigureAwait(false),
      LspSymbolCommand.CallHierarchyOutgoing c => await service.CallHierarchyOutgoingAsync(ToUri(c.Path), c.SymbolName, c.Occurrence, c.Depth, ct).ConfigureAwait(false),
      _ => throw new InvalidOperationException("unreachable: command is a validated record family"),
    };
    return Map(result);
  }

  private static string ToUri(string path) => LspUriMapper.ToUri(path);

  private static LspSymbolOutcome Map(LspSymbolResult result)
  {
    string message = result.Message ?? result.State.ToString();
    List<ToolDomain.Lsp.LspSymbolSite> sites = [.. result.Sites.Select(s => new ToolDomain.Lsp.LspSymbolSite(FromUri(s.Uri), s.Line, s.Character, s.SymbolName))];
    return result.State switch
    {
      LspSymbolState.Ready => new LspSymbolOutcome.Ready(sites, result.OccurrenceUsed, result.TotalOccurrences),
      LspSymbolState.Unsupported => new LspSymbolOutcome.Unsupported(message),
      LspSymbolState.Unavailable => new LspSymbolOutcome.Unavailable(message),
      LspSymbolState.Starting => new LspSymbolOutcome.Starting(message),
      LspSymbolState.Stale => new LspSymbolOutcome.Stale(message),
      LspSymbolState.Degraded => new LspSymbolOutcome.Degraded(message),
      _ => new LspSymbolOutcome.Degraded(message),
    };
  }

  private static string FromUri(string uri)
  {
    try { return LspUriMapper.FromUri(uri); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException) { return uri; }
  }
}
