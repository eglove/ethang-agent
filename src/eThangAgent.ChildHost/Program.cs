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

// Accept loop (R3.1 re-attach): each app connection is served until it drops, then
// the host accepts the NEXT connection — children keep running across app restarts,
// and the fresh app re-attaches on the same pipe. ServeAsync declares the live set on
// every connection entry, so the re-attaching app learns exact ownership (FR-L8).
ChildHostServer server = new(settingsPath, databasePath);
while (true)
{
  NamedPipeChildTransport transport;
  try
  {
    transport = await NamedPipeChildTransport.AcceptAppAsync(pipeName);
  }
  // Named decision (CA1031): the accept loop is the host's liveness boundary — any
  // single accept failure (e.g. pipe-name collision during teardown) must not end
  // the host; it reports and retries.
#pragma warning disable CA1031 // Do not catch general exception types
  catch (Exception ex)
  {
    Console.Error.WriteLine("accept-failed: " + ex.Message);
    await Task.Delay(200).ConfigureAwait(false);
    continue;
  }
#pragma warning restore CA1031

  Console.Out.WriteLine("app-connected");
  server.AttachTransport(transport);
  await server.ServeAsync();
  await transport.DisposeAsync().ConfigureAwait(false); // free the pipe name for the next accept
  Console.Out.WriteLine("app-disconnected");
}
