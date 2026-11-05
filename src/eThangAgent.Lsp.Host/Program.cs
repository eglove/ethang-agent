using System.Diagnostics;
using eThangAgent.Lsp.ACL;
using eThangAgent.Lsp.Host;

if (args.Length < 3)
{
  Console.Error.WriteLine("usage: eThangAgent.Lsp.Host <pipeName> <workspaceId> <workspaceRoot>");
  return 2;
}

string pipeName = args[0];
string workspaceId = args[1];
string workspaceRoot = args[2];

Console.Out.WriteLine("daemon-starting " + pipeName);
Console.Out.Flush();

string appDir = AppContext.BaseDirectory;
LspServerLaunch? launch = null;
if (LspLanguageDetector.DetectLanguage(workspaceRoot) == "csharp")
{
  Console.Out.WriteLine("server-resolving " + workspaceId);
  Console.Out.Flush();
  launch = await LspServerProvisioner.CreateDefault(appDir, workspaceRoot)
    .ResolveOrInstallAsync(LspServerCatalog.CSharp, CancellationToken.None);
  Console.Out.WriteLine(launch is null ? "server-unavailable" : "server-ready " + launch.Version);
  Console.Out.Flush();
}
Process? serverProcess = null;
LspJsonRpcTransport? transport = null;
LspDiagnosticStore store = new();
LspDiagnosticsService diagnosticsService = new(null, store);
LspSymbolService symbolService = new(null, LspCapabilities.None);
LspClient? lspClient = null;

if (launch is not null)
{
  ProcessStartInfo psi = LspServerLauncher.BuildStartInfo(launch);
  serverProcess = Process.Start(psi);
  if (serverProcess is not null)
  {
    transport = new LspJsonRpcTransport(serverProcess.StandardOutput.BaseStream, serverProcess.StandardInput.BaseStream, TimeSpan.FromSeconds(5));
    LspNotificationRouter router = new(store);
    transport.OnNotification(_ => router.HandleNotification(_));
    LspServerConfig config = LspLanguageDetector.CSharpConfig(launch.ServerPath);
    LspClient client = new(transport, config);
    lspClient = client;
    string wsUri = LspUriMapper.ToUri(workspaceRoot);
    _ = await client.InitializeAsync(wsUri);
    diagnosticsService = new LspDiagnosticsService(transport, store, client.Capabilities);
    symbolService = new LspSymbolService(transport, client.Capabilities);
  }
}

ILspDocumentSync sink = transport is null ? new NoOpDocumentSync() : new LspTransportDocumentSync(transport);
LspServerConfig syncConfig = launch is null
  ? new LspServerConfig("csharp", "", [], [".cs"])
  : LspLanguageDetector.CSharpConfig(launch.ServerPath);
LspDocumentSync forwarder = new(workspaceId, workspaceRoot, syncConfig.LanguageId, syncConfig.Extensions, sink);

using LspDaemonServer server = new(pipeName, workspaceId, () => Task.CompletedTask);
server.AttachForwarder(forwarder);
server.AttachDiagnosticsService(diagnosticsService);
server.AttachSymbolService(symbolService);
server.AttachRestartHook(async ct =>
{
  diagnosticsService.BeginRestart();
  try
  {
    await forwarder.ReopenAllAsync(forwarder.OpenDocuments, ct).ConfigureAwait(false);
  }
  finally
  {
    await diagnosticsService.CompleteRestartAsync().ConfigureAwait(false);
  }
});
try
{
  await server.ServeUntilClosedAsync();
}
catch (Exception ex)
{
  Console.Error.WriteLine("daemon-failed: " + ex.Message);
  return 1;
}
finally
{
  if (serverProcess is not null && !serverProcess.HasExited)
  {
    try { serverProcess.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* server already exited */ }
  }
}

Console.Out.WriteLine("daemon-ready " + workspaceId);
Console.Out.Flush();
return 0;
