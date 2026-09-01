using eThangAgent.ChildHost;
using eThangAgent.Transport.ACL;

// ChildHost: the supervised out-of-process child host (T4/T9c).
//   args[0] = pipe name; args[1] = settings JSON path; args[2] = app database path.
// The host owns ONE named-pipe connection to the app, runs every child through the real
// spawner stack against the shared app database, and streams settlements back. Budgets are
// enforced here too (FR-X5): the host refuses starts beyond its own concurrency ceiling.
if (args.Length < 3)
{
  Console.Error.WriteLine("usage: eThangAgent.ChildHost <pipeName> <settingsJsonPath> <databasePath>");
  return 2;
}

string pipeName = args[0];
string settingsPath = args[1];
string databasePath = args[2];

Console.Out.WriteLine("host-starting " + pipeName);
NamedPipeChildTransport transport = await NamedPipeChildTransport.AcceptAppAsync(pipeName);
Console.Out.WriteLine("app-connected");

ChildHostServer server = new(transport, settingsPath, databasePath);
await server.ServeAsync();
return 0;
