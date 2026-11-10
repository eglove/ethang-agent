using System.Diagnostics;
using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL;

/// <summary>How a <see cref="LspServerSet"/> obtains a language server's transport. The
///     production starter launches the server process; a test supplies a fake, so the set's
///     routing, containment, and disposal are exercised with no process at all.</summary>
public interface ILspServerStarter
{
  Task<LspServerConnection> StartAsync(LspServerLaunch launch, string workspaceRoot, CancellationToken ct);
}

/// <summary>One started server: its transport, the client that completed the initialize
///     handshake, and the process behind it (null when the starter produced no process).
///     The set owns the process from here and kills it on disposal.</summary>
public sealed class LspServerConnection : IDisposable
{
  private readonly Process? _process;
  private readonly Action? _release;

  public LspServerConnection(
      LspJsonRpcTransport transport,
      LspClient client,
      Process? process = null,
      Action? release = null)
  {
    Transport = transport ?? throw new ArgumentNullException(nameof(transport));
    Client = client ?? throw new ArgumentNullException(nameof(client));
    _process = process;
    _release = release;
  }

  public LspJsonRpcTransport Transport { get; }

  public LspClient Client { get; }

  /// <summary>The server process, or null when the starter produced none.</summary>
  public Process? Process => _process;

  public void Dispose()
  {
    Transport.Close();
    if (_process is not null)
    {
      try
      {
        if (!_process.HasExited)
        {
          _process.Kill(entireProcessTree: true);
        }
      }
      catch (InvalidOperationException)
      {
      }
      catch (System.ComponentModel.Win32Exception)
      {
      }

      _process.Dispose();
    }

    _release?.Invoke();
  }
}

/// <summary>One language's live server: the launch that produced it, the transport it speaks
///     on, the client whose capabilities gate its queries, the document sync that forwards
///     its files, and the services a request is answered from.</summary>
public sealed class LspRunningServer : IDisposable
{
  private readonly LspServerConnection _connection;

  public LspRunningServer(
      LspLanguageRow row,
      LspServerLaunch launch,
      LspServerConnection connection,
      LspDocumentSync documents,
      ILspDiagnosticsQuery diagnostics,
      ILspSymbolQuery symbols)
  {
    Row = row ?? throw new ArgumentNullException(nameof(row));
    Launch = launch ?? throw new ArgumentNullException(nameof(launch));
    _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    Documents = documents ?? throw new ArgumentNullException(nameof(documents));
    Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    Symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
  }

  /// <summary>The catalog row this server was started for.</summary>
  public LspLanguageRow Row { get; }

  public string LanguageId => Row.LanguageId;

  public LspServerLaunch Launch { get; }

  /// <summary>The started connection this server answers over.</summary>
  public LspServerConnection Connection => _connection;

  public LspJsonRpcTransport Transport => _connection.Transport;

  public LspClient Client => _connection.Client;

  public IReadOnlyList<string> Extensions => Row.Extensions;

  public LspDocumentSync Documents { get; }

  public ILspDiagnosticsQuery Diagnostics { get; }

  public ILspSymbolQuery Symbols { get; }

  /// <summary>True when <paramref name="path"/> carries an extension this server's row claims.</summary>
  public bool Claims(string path) => Row.Claims(path);

  /// <summary>Stops the server process and closes its transport.</summary>
  public void Dispose() => _connection.Dispose();
}

/// <summary>Every language server one workspace daemon is running, one per installed and
///     detected language, with no cap on the count. A request is answered by the server whose
///     row claims the path's extension; a path no running server claims is answered empty and
///     without an error, because a workspace's mixed content is ordinary rather than a fault.
///     <para>A server that cannot start is left out of the set and the others keep serving:
///     one language's broken install must not cost the workspace every other language.</para>
///     <para>Shutting the set down kills every process it started.</para></summary>
public sealed class LspServerSet : IDisposable
{
  private readonly List<LspRunningServer> _servers;
  private readonly IReadOnlyList<LspServerLaunch> _failed;

  private LspServerSet(List<LspRunningServer> servers, IReadOnlyList<LspServerLaunch> failed)
  {
    _servers = servers;
    _failed = failed;
  }

  /// <summary>A set over servers a caller already holds, so the routing can be exercised
  ///     against servers whose services are recorders rather than a language server.</summary>
  internal static LspServerSet FromServers(IReadOnlyList<LspRunningServer> servers)
  {
    ArgumentNullException.ThrowIfNull(servers);
    return new LspServerSet([.. servers], []);
  }

  /// <summary>A set holding nothing: every request is answered empty and no process exists.
  ///     </summary>
  public static LspServerSet Empty { get; } = new([], []);

  /// <summary>The running servers, in the order they were started.</summary>
  public IReadOnlyList<LspRunningServer> Servers => _servers;

  /// <summary>The launches that produced no server, so nothing serves their languages.</summary>
  public IReadOnlyList<LspServerLaunch> Failed => _failed;

  public int Count => _servers.Count;

  /// <summary>Starts one server per launch. A launch that throws is recorded in
  ///     <see cref="Failed"/> and the remaining launches still start.</summary>
  public static async Task<LspServerSet> StartAsync(
      IReadOnlyList<LspServerLaunch> launches,
      string workspaceId,
      string workspaceRoot,
      ILspServerStarter starter,
      CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(launches);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    ArgumentNullException.ThrowIfNull(starter);
    List<LspRunningServer> started = [];
    List<LspServerLaunch> failed = [];
    foreach (LspServerLaunch launch in launches)
    {
      LspRunningServer? server = await StartOneAsync(launch, workspaceId, workspaceRoot, starter, ct)
          .ConfigureAwait(false);
      if (server is null)
      {
        failed.Add(launch);
        continue;
      }

      started.Add(server);
    }

    return new LspServerSet(started, failed);
  }

  /// <summary>Starts one server with the production starter: the row's own command line,
  ///     the process's stdio, and the initialize handshake.</summary>
  public static Task<LspServerSet> StartAsync(
      IReadOnlyList<LspServerLaunch> launches,
      string workspaceId,
      string workspaceRoot,
      CancellationToken ct) =>
      StartAsync(launches, workspaceId, workspaceRoot, new LspProcessServerStarter(), ct);

  private static async Task<LspRunningServer?> StartOneAsync(
      LspServerLaunch launch,
      string workspaceId,
      string workspaceRoot,
      ILspServerStarter starter,
      CancellationToken ct)
  {
    LspLanguageRow? row = LspServerCatalog.ForLanguage(launch.LanguageId);
    if (row is null)
    {
      return null;
    }

    LspServerConnection connection;
    try
    {
      connection = await starter.StartAsync(launch, workspaceRoot, ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException
        or System.ComponentModel.Win32Exception or OperationCanceledException)
    {
      return null;
    }

    return Wire(row, launch, connection, workspaceId, workspaceRoot);
  }

  /// <summary>Builds one running server over a started connection, or stops the connection and
  ///     reports the language as failed when the server cannot be wired.</summary>
  private static LspRunningServer? Wire(
      LspLanguageRow row,
      LspServerLaunch launch,
      LspServerConnection connection,
      string workspaceId,
      string workspaceRoot)
  {
    try
    {
      LspDiagnosticStore store = new();
      ILspDocumentSync sink = new LspTransportDocumentSync(connection.Transport);
      LspDocumentSync documents = new(workspaceId, workspaceRoot, row.LanguageId, row.Extensions, sink);
      LspNotificationRouter router = new(store);
      LspRoslynWorkspaceModel? model = row.LanguageId == LspServerCatalog.CSharp.LanguageId
          ? new LspRoslynWorkspaceModel(connection.Transport, documents, workspaceRoot)
          : null;
      connection.Transport.OnNotification(json =>
      {
        _ = router.HandleNotification(json);
        if (model is not null)
        {
          _ = model.HandleNotification(json);
        }
      });
      LspDiagnosticsService diagnostics = new(connection.Transport, store, connection.Client.Capabilities, model, row.LanguageId);
      LspSymbolService symbols = new(connection.Transport, connection.Client.Capabilities, model, row.LanguageId);
      return new LspRunningServer(
          row, launch, connection, documents,
          new LspServerDiagnosticsQuery(diagnostics),
          new LspServerSymbolQuery(symbols));
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException
        or System.ComponentModel.Win32Exception or OperationCanceledException)
    {
      connection.Dispose();
      return null;
    }
  }

  /// <summary>The server claiming <paramref name="path"/>'s extension, or null when none is
  ///     running for it.</summary>
  public LspRunningServer? ForPath(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    string? languageId = LspLanguageRouter.ForPath(path);
    return languageId is null ? null : ForLanguage(languageId);
  }

  /// <summary>The running server for one language, or null when that language has none.
  ///     </summary>
  public LspRunningServer? ForLanguage(string? languageId)
  {
    if (string.IsNullOrWhiteSpace(languageId))
    {
      return null;
    }

    foreach (LspRunningServer server in _servers)
    {
      if (string.Equals(server.LanguageId, languageId, StringComparison.OrdinalIgnoreCase))
      {
        return server;
      }
    }

    return null;
  }

  /// <summary>The diagnostics the server claiming <paramref name="path"/> reports. A path no
  ///     running server claims is answered with an empty Ready result: nothing is wrong with
  ///     the file, there is simply no server that would know.</summary>
  public async Task<LspDiagnosticsResult> GetDiagnosticsAsync(string path, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    LspRunningServer? server = ForPath(path);
    if (server is null)
    {
      return new LspDiagnosticsResult(LspDiagnosticsState.Ready, []);
    }

    return await server.Diagnostics.PullAsync(LspUriMapper.ToUri(path), ct).ConfigureAwait(false);
  }

  /// <summary>The symbol answer from the server claiming the document's extension. A path no
  ///     running server claims is answered with an empty Ready result, never an error.
  ///     <para>An action that names no document is the SET's, not one server's: a workspace
  ///     load asks every server to load its workspace, because routing by a path no server
  ///     claims would answer Ready while no server had been asked anything.</para></summary>
  public async Task<LspSymbolResult> QuerySymbolAsync(
      string action, string uri, string symbolName, int occurrence, int depth, string newName,
      CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(action);
    if (string.Equals(action, WorkspaceLoadAction, StringComparison.Ordinal))
    {
      return await LoadEveryServerAsync(occurrence, depth, ct).ConfigureAwait(false);
    }

    LspRunningServer? server = ServerForUri(uri);
    if (server is null)
    {
      return new LspSymbolResult(LspSymbolState.Ready, [], occurrence, 1);
    }

    return await server.Symbols
        .ExecuteAsync(action, uri, symbolName, occurrence, depth, newName, ct)
        .ConfigureAwait(false);
  }

  /// <summary>The action that asks a server to load its workspace rather than answer about a
  ///     document.</summary>
  private const string WorkspaceLoadAction = "workspace-load";

  /// <summary>Asks every running server to load its workspace and reports the first one that
  ///     has not: a caller that sees Ready learns that every language is loaded, and a caller
  ///     that sees Starting learns which one is not.</summary>
  private async Task<LspSymbolResult> LoadEveryServerAsync(int occurrence, int depth, CancellationToken ct)
  {
    LspSymbolResult result = new(LspSymbolState.Ready, [], occurrence, 1);
    foreach (LspRunningServer server in _servers)
    {
      LspSymbolResult answered = await server.Symbols
          .ExecuteAsync(WorkspaceLoadAction, string.Empty, string.Empty, occurrence, depth, string.Empty, ct)
          .ConfigureAwait(false);
      if (answered.State != LspSymbolState.Ready)
      {
        return answered;
      }

      result = answered;
    }

    return result;
  }

  /// <summary>Forwards one watcher announcement to every running server. Each server applies
  ///     only the paths its own extensions claim, so a workspace holding two languages sends
  ///     each file to exactly one of them.</summary>
  public async Task ApplyAnnouncementAsync(WatcherAnnouncement announcement, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(announcement);
    foreach (LspRunningServer server in _servers)
    {
      await server.Documents.ApplyAsync(announcement, ct).ConfigureAwait(false);
    }
  }

  /// <summary>Re-opens every server's open documents from current disk text, so no query is
  ///     answered against a stale server model after a restart. Each server's diagnostics
  ///     queries are held for the duration, so a query cannot observe a half-reopened model.
  ///     </summary>
  public async Task ReopenAllAsync(CancellationToken ct = default)
  {
    foreach (LspRunningServer server in _servers)
    {
      server.Diagnostics.BeginRestart();
      try
      {
        await server.Documents.ReopenAllAsync(server.Documents.OpenDocuments, ct).ConfigureAwait(false);
      }
      finally
      {
        await server.Diagnostics.CompleteRestartAsync().ConfigureAwait(false);
      }
    }
  }

  private LspRunningServer? ServerForUri(string uri)
  {
    if (string.IsNullOrWhiteSpace(uri))
    {
      return null;
    }

    string path;
    try
    {
      path = LspUriMapper.FromUri(uri);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException)
    {
      return null;
    }

    return ForPath(path);
  }

  /// <summary>Stops every server this set started. A server already gone is a no-op.
  ///     </summary>
  public void Dispose()
  {
    foreach (LspRunningServer server in _servers)
    {
      server.Dispose();
    }

    _servers.Clear();
  }
}

/// <summary>Starts a language server as its own process over stdio, with the command line its
///     row records, and completes the initialize handshake. The process is killed through
///     <see cref="LspServerConnection"/> when the set is disposed.</summary>
public sealed class LspProcessServerStarter : ILspServerStarter
{
  private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(30);

  public async Task<LspServerConnection> StartAsync(
      LspServerLaunch launch, string workspaceRoot, CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(launch);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    LspLanguageRow row = LspServerCatalog.ForLanguage(launch.LanguageId)
        ?? throw new InvalidOperationException("The catalog carries no " + launch.LanguageId + " row.");
    Process process = Process.Start(LspServerLauncher.BuildStartInfo(launch))
        ?? throw new InvalidOperationException("the " + launch.LanguageId + " language server could not be started");
    LspJsonRpcTransport transport = new(
        process.StandardOutput.BaseStream, process.StandardInput.BaseStream, InitializeTimeout);
    LspClient client = new(transport, new LspServerConfig(row.LanguageId, launch.ServerPath, row.Arguments, row.Extensions));
    try
    {
      LspRequestResult initialized = await client
          .InitializeAsync(LspUriMapper.ToUri(workspaceRoot), ct).ConfigureAwait(false);
      if (!initialized.IsSuccess)
      {
        throw new InvalidOperationException(
            "the " + launch.LanguageId + " language server refused initialize: " + initialized.ErrorMessage);
      }
    }
    catch
    {
      transport.Close();
      try
      {
        if (!process.HasExited)
        {
          process.Kill(entireProcessTree: true);
        }
      }
      catch (InvalidOperationException)
      {
      }

      process.Dispose();
      throw;
    }

    return new LspServerConnection(transport, client, process);
  }
}

/// <summary>One running server's diagnostics service, behind <see cref="ILspDiagnosticsQuery"/>.
///     </summary>
public sealed class LspServerDiagnosticsQuery : ILspDiagnosticsQuery
{
  private readonly LspDiagnosticsService _service;

  public LspServerDiagnosticsQuery(LspDiagnosticsService service)
      => _service = service ?? throw new ArgumentNullException(nameof(service));

  public Task<LspDiagnosticsResult> PullAsync(string uri, CancellationToken ct = default)
      => _service.PullAsync(uri, ct);

  public void BeginRestart() => _service.BeginRestart();

  public Task CompleteRestartAsync() => _service.CompleteRestartAsync();
}

/// <summary>One running server's symbol service, behind <see cref="ILspSymbolQuery"/>.
///     </summary>
public sealed class LspServerSymbolQuery : ILspSymbolQuery
{
  private readonly LspSymbolService _service;

  public LspServerSymbolQuery(LspSymbolService service)
      => _service = service ?? throw new ArgumentNullException(nameof(service));

  public Task<LspSymbolResult> ExecuteAsync(
      string action, string uri, string symbolName, int occurrence, int depth, string newName,
      CancellationToken ct = default)
      => _service.ExecuteAsync(action, uri, symbolName, occurrence, depth, newName, ct);
}
