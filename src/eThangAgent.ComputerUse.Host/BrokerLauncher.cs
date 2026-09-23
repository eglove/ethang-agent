namespace eThangAgent.ComputerUse.Host;

/// <summary>The broker launcher's resolved configuration (task 16): the pipe to serve,
///     the auth token, the bounded log path, and either a ready-to-run config or the
///     exit code a configuration error produces. Bad configuration is DATA, never a
///     crash dump: the process exits with code 2 and a last-gasp log line.</summary>
public sealed record BrokerLaunchOptions(
  string PipeName,
  string Token,
  string LogFilePath,
  string? Error,
  int ExitCode)
{
  /// <summary>Resolves the launch configuration: the pipe name from argv[0] (or the
  ///     ETHANG_COMPUTER_USE_PIPE environment variable when no argv carries it), the
  ///     token from ETHANG_COMPUTER_USE_TOKEN, and the log path under the per-user app
  ///     data dir. The env lookup and argv are injected so tests never touch process state.
  ///     The token is NEVER logged and never appears in an error message.</summary>
  public static BrokerLaunchOptions Resolve(string[] args, Func<string, string?> tokenEnv, Func<string, string?> anyEnv)
  {
    ArgumentNullException.ThrowIfNull(args);
    ArgumentNullException.ThrowIfNull(tokenEnv);
    ArgumentNullException.ThrowIfNull(anyEnv);
    string pipeName = args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? string.Empty;
    if (string.IsNullOrWhiteSpace(pipeName))
    {
      pipeName = anyEnv(PipeNameEnvVar) ?? string.Empty;
    }

    if (string.IsNullOrWhiteSpace(pipeName))
    {
      return new BrokerLaunchOptions(string.Empty, string.Empty, DefaultLogPath(),
        "no pipe name: pass it as the first argument or set " + PipeNameEnvVar + ".", 2);
    }

    string token = tokenEnv(TokenEnvVar) ?? string.Empty;
    return string.IsNullOrWhiteSpace(token)
      ? new BrokerLaunchOptions(pipeName, string.Empty, DefaultLogPath(), "no auth token: set " + TokenEnvVar + ".", 2)
      : new BrokerLaunchOptions(pipeName, token, DefaultLogPath(), null, 0);
  }

  /// <summary>The environment variable carrying the per-spawn auth token (wire contract).</summary>
  public const string TokenEnvVar = "ETHANG_COMPUTER_USE_TOKEN";

  /// <summary>The environment variable carrying the pipe name when argv is unavailable.</summary>
  public const string PipeNameEnvVar = "ETHANG_COMPUTER_USE_PIPE";

  /// <summary>The bounded log file: under the per-user app data dir, one file per pipe
  ///     (per broker instance), never stdout.</summary>
  public static string DefaultLogPath()
  {
    string dir = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "eThangAgent", "computer-use");
    return Path.Combine(dir, "broker.log");
  }
};

/// <summary>The broker's bounded file log (task 16): append-only text with a hard cap.
///     When the cap is reached the log ROLLS: the file is halved (the newest half is
///     kept) so the file stays bounded without losing the most recent diagnostics.
///     Logging must never throw into the serve path: failures are swallowed by design.
///     NEVER stdout: the supervisor owns the process; stdout is not a log sink.</summary>
public sealed class BoundedFileLog(string path)
{
  /// <summary>The maximum bytes the log file may occupy before it rolls (64 KiB -
  ///     diagnostics, not telemetry).</summary>
  public const long MaxBytes = 64 * 1024;

  private readonly Lock _gate = new();

  /// <summary>Appends one line (a timestamp is added). Bounded: over the cap, the older
  ///     half of the file is dropped. Best-effort by contract.</summary>
  public void Write(string line)
  {
    ArgumentNullException.ThrowIfNull(line);
    lock (_gate)
    {
      _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
      {
        RollFile();
      }

      File.AppendAllText(path, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
    }
  }

  /// <summary>Keeps the NEWEST half of the log (the tail), drops the older half.</summary>
  private void RollFile()
  {
    try
    {
      string[] lines = File.ReadAllLines(path);
      string[] tail = [.. lines.Skip(lines.Length / 2)];
      File.WriteAllLines(path, tail);
    }
    catch (IOException)
    {
      // A failed roll is the same best-effort contract as a failed append.
    }
  }
};
