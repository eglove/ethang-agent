using System.IO.Pipes;
using eThangAgent.ComputerUse.Host;

// ComputerUse.Host: the computer-use broker process (tasks 16-18).
//   args[0] = pipe name (the ACL spawns us with it; ETHANG_COMPUTER_USE_PIPE is the
//   env fallback); the auth token arrives via ETHANG_COMPUTER_USE_TOKEN. Logs go to a
//   bounded file under the app data dir - NEVER stdout (the supervisor owns the
//   process; stdout is not a log sink).

// W-c: PerMonitorV2 DPI awareness - coordinate math must run on physical pixels. Called
// BEFORE any window/capture work.
_ = DpiAwareness.SetPerMonitorV2();

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

// Task 18: the REAL observation surface (UIA walk, window list, app identity, capture)
// plus the element-op resolver the input dispatcher uses for element_* methods (R1).
// With --input-targeted the send hooks deliver input as window messages to the verified
// foreground target (TargetedInputDelivery) instead of global SendInput — the
// integration suite's opt-in; every gate still runs on the real foreground read.
RealBrokerObserver observer = new();
PipeServer broker = options.TargetedInput
    ? new PipeServer(new BrokerConfig(options.PipeName, options.Token), observer,
        sendChord: TargetedInputDelivery.SendChord,
        sendText: TargetedInputDelivery.SendText,
        sendDrag: TargetedInputDelivery.SendDrag,
        sendMouseButtonAt: TargetedInputDelivery.SendMouseButtonAt,
        sendWheelAt: TargetedInputDelivery.SendWheelAt,
        targetedInput: true)
    : new PipeServer(new BrokerConfig(options.PipeName, options.Token), observer);
broker.InputDispatch.SetElementOps(observer.CreateElementOps());

// C6 (production): one broker per workspace serves concurrent connections for the broker's
// lifetime - no serve-then-exit. The factory mints a fresh multi-instance pipe per accepted
// connection; the accept loop runs until cancelled (process shutdown).
using CancellationTokenSource shutdown = new();
Console.CancelKeyPress += (_, e) =>
{
  e.Cancel = true;
  shutdown.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

await PipeServeLoop.ServeConcurrentAsync(
    () => new NamedPipeServerStream(options.PipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous),
    broker,
    shutdown.Token).ConfigureAwait(false);
log.Write("shutdown; broker exiting");
return 0;
