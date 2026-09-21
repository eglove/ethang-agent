using System.IO.Pipes;
using eThangAgent.ComputerUse.Host;

// ComputerUse.Host: the computer-use broker process (tasks 16-17).
//   args[0] = pipe name (the ACL spawns us with it; ETHANG_COMPUTER_USE_PIPE is the
//   env fallback); the auth token arrives via ETHANG_COMPUTER_USE_TOKEN. Logs go to a
//   bounded file under the app data dir - NEVER stdout (the supervisor owns the
//   process; stdout is not a log sink).
BrokerLaunchOptions options = BrokerLaunchOptions.Resolve(args,
  Environment.GetEnvironmentVariable,
  Environment.GetEnvironmentVariable);
BoundedFileLog log = new(options.LogFilePath);
if (options.Error is not null)
{
  log.Write("launch-failed: " + options.Error);
  return options.ExitCode;
}

log.Write("serving pipe " + options.PipeName);
PipeServer broker = new(new BrokerConfig(options.PipeName, options.Token));
using NamedPipeServerStream server = new(options.PipeName, PipeDirection.InOut, 1,
  PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
await PipeServeLoop.ServeOnceAsync(server, broker).ConfigureAwait(false);
log.Write("connection ended; broker exiting");
return 0;
