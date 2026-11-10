using eThangAgent.Lsp.ACL;

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
LspServerProvisioner provisioner = LspServerProvisioner.CreateDefault(appDir, workspaceRoot);

// The workspace's servers, started once. The restart hook reuses this set instead of
// starting a second one: every accepted connection runs the hook, so a hook that started
// its own servers would launch a duplicate set per connection and leak the previous one.
LspServerSet? started = null;

using LspDaemonServer server = new(pipeName, workspaceId, () => Task.CompletedTask);
server.AttachServers(StartWorkspaceServersAsync);
server.AttachRestartHook(async ct =>
{
  LspServerSet servers = await StartWorkspaceServersAsync(ct);
  await servers.ReopenAllAsync(ct);
});
try
{
  await server.ServeUntilClosedAsync(server.ShutdownToken);
}
catch (Exception ex)
{
  Console.Error.WriteLine("daemon-failed: " + ex.Message);
  return 1;
}
finally
{
  started?.Dispose();
}

Console.Out.WriteLine("daemon-ready " + workspaceId);
Console.Out.Flush();
return 0;

async Task<LspServerSet> StartWorkspaceServersAsync(CancellationToken ct)
{
  if (started is not null)
  {
    return started;
  }

  List<LspServerLaunch> launches = [];
  foreach (DetectedLanguage detected in LspWorkspaceLanguages.Detect(workspaceRoot))
  {
    LspLanguageRow? row = LspServerCatalog.ForLanguage(detected.LanguageId);
    if (row is null)
    {
      continue;
    }

    Console.Out.WriteLine("server-resolving " + workspaceId);
    Console.Out.Flush();
    LspServerLaunch? launch = row.LanguageId == LspServerCatalog.CSharp.LanguageId
        ? await provisioner.ResolveOrInstallAsync(row, ct)
        : provisioner.ResolveExisting(row);
    if (launch is null)
    {
      Console.Out.WriteLine("server-unavailable");
      Console.Out.Flush();
      continue;
    }

    Console.Out.WriteLine("server-ready " + launch.Version);
    Console.Out.Flush();
    launches.Add(launch);
  }

  started = await LspServerSet.StartAsync(launches, workspaceId, workspaceRoot, ct);
  return started;
}
