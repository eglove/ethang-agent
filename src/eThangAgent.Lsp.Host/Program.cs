using eThangAgent.Lsp.ACL;

if (args.Length < 3)
{
  Console.Error.WriteLine("usage: eThangAgent.Lsp.Host <pipeName> <workspaceId> <workspaceRoot>");
  return 2;
}

string pipeName = args[0];
string workspaceId = args[1];
_ = args[2];

Console.Out.WriteLine("daemon-starting " + pipeName);
Console.Out.Flush();

using LspDaemonServer server = new(pipeName, workspaceId, () => Task.CompletedTask);
try
{
  await server.ServeOnceAsync();
}
catch (Exception ex)
{
  Console.Error.WriteLine("daemon-failed: " + ex.Message);
  return 1;
}

Console.Out.WriteLine("daemon-ready " + workspaceId);
Console.Out.Flush();
return 0;
