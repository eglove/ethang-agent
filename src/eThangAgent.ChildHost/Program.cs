using eThangAgent.ChildHost;
using eThangAgent.Transport.ACL;

// ChildHost: the supervised out-of-process child host (T4/T9c).
//   args[0] = pipe name; args[1] = settings JSON path; args[2] = app database path.
// The host owns ONE named-pipe connection to the app, runs every child through the real
// spawner stack against the shared app database, and streams settlements back. Budgets are
// enforced here too (FR-X5): the host refuses starts beyond its own concurrency ceiling.
//
// ONE CONNECTION, THEN EXIT (issue #274). The app contains this process in a kill-on-close
// Job Object, so the host dies with the app - clean exit or crash. There is therefore no
// re-attach to serve: the previous accept loop outlived a dropped connection so a
// relaunching app could find the host and adopt the children it kept running, and that
// behavior is retired. A conversation continues by resuming its persisted session from the
// Sessions catalog, which is the durable record. When the app's connection drops, the host
// has lost the only client it will ever have and exits.
if (args.Length < 3)
{
  Console.Error.WriteLine("usage: eThangAgent.ChildHost <pipeName> <settingsJsonPath> <databasePath>");
  return 2;
}

string pipeName = args[0];
string settingsPath = args[1];
string databasePath = args[2];

Console.Out.WriteLine("host-starting " + pipeName);

ChildHostServer server = new(settingsPath, databasePath);

// Wait for the app's ONE connection. A failed accept is NOT retried: retrying would create a
// fresh pipe instance while a client may already hold the abandoned one, stranding that
// client against a host that will never read from it. An accept failure therefore ends the
// process, and the app's connect deadline turns that into a named attach failure it can
// report - the honest outcome, and the one that cannot hang.
NamedPipeChildTransport transport;
// Named decision (CA1031): the accept is the host's liveness boundary.
#pragma warning disable CA1031 // Do not catch general exception types
try
{
  transport = await NamedPipeChildTransport.AcceptAppAsync(pipeName);
}
catch (Exception ex)
{
  Console.Error.WriteLine("accept-failed: " + ex.Message);
  return 1;
}
#pragma warning restore CA1031

Console.Out.WriteLine("app-connected");
server.AttachTransport(transport);
await server.ServeAsync();
await transport.DisposeAsync().ConfigureAwait(false);
Console.Out.WriteLine("app-disconnected");
// ONE connection served: the app contains this process, so there is no second client and
// nothing left to wait for (issue #274).
return 0;
