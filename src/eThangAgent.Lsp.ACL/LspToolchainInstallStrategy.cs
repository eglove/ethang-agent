using System.Diagnostics;

namespace eThangAgent.Lsp.ACL;

/// <summary>One toolchain invocation: the executable to spawn, its arguments as separate
///     elements, and any environment it needs. Arguments are a list rather than a command
///     line, so a path holding a space reaches the toolchain as one argument.</summary>
public sealed record LspToolchainCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>Installs a row through a toolchain the machine provides. The prerequisite is
///     looked up before anything runs, and a machine without it gets no process at all. The
///     command installs into an app-owned prefix, never into a global toolchain directory,
///     and the install only counts when the expected entry point is on disk afterwards.
///     <para>THE LAUNCHER LANDS AT THE PREFIX ROOT: every command is built so the launcher it
///     produces lands where <see cref="LspInstallLayout.EntryPoint"/> looks for it, which for
///     a toolchain row is the prefix itself.</para>
///     <para>A row's companion packages are installed into the same prefix by the same
///     command, so a server that resolves another package at run time finds it.</para></summary>
public sealed class LspToolchainInstallStrategy : ILspInstallStrategy
{
  private readonly LspPrerequisiteProbe _probe;
  private readonly Func<LspLanguageRow, string, string, LspToolchainCommand> _buildCommand;
  private readonly Func<LspToolchainCommand, CancellationToken, Task<int>> _run;

  public LspToolchainInstallStrategy(
      LspPrerequisiteProbe probe,
      Func<LspLanguageRow, string, string, LspToolchainCommand>? buildCommand = null,
      Func<LspToolchainCommand, CancellationToken, Task<int>>? run = null)
  {
    _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    _buildCommand = buildCommand ?? BuildCommand;
    _run = run ?? RunToExitAsync;
  }

  /// <summary>The default strategy: the real PATH probe and a directly spawned toolchain.</summary>
  public static LspToolchainInstallStrategy CreateDefault() =>
      new(LspPrerequisiteProbe.CreateDefault());

  public async Task<LspInstallResult> InstallAsync(
      LspLanguageRow row,
      string installRoot,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(row);
    ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
    if (ResolveInstalled(row, installRoot) is not null)
    {
      return new LspInstallResult(LspInstallOutcome.Installed, row);
    }

    PrerequisiteProbeResult host = Probe(row);
    if (host.Status != PrerequisiteStatus.Present || string.IsNullOrWhiteSpace(host.ExecutablePath))
    {
      return new LspInstallResult(LspInstallOutcome.PrerequisiteMissing, row);
    }

    string prefix = LspInstallLayout.ToolchainPrefix(row, installRoot);
    Directory.CreateDirectory(prefix);
    LspToolchainCommand command = _buildCommand(row, host.ExecutablePath, prefix);
    int exitCode = await _run(command, cancellationToken).ConfigureAwait(false);
    if (exitCode != 0)
    {
      return new LspInstallResult(LspInstallOutcome.DownloadFailed, row,
          $"{Path.GetFileName(command.Executable)} exited with code {exitCode} while installing "
          + $"{row.PackageId} {row.Version}.");
    }

    string? installed = ResolveInstalled(row, installRoot);
    if (installed is null)
    {
      return new LspInstallResult(LspInstallOutcome.DownloadFailed, row,
          $"{Path.GetFileName(command.Executable)} reported success but "
          + $"{LspInstallLayout.EntryPoint(row, prefix)} is not on disk.");
    }

    return new LspInstallResult(LspInstallOutcome.Installed, row);
  }

  public string? ResolveInstalled(LspLanguageRow row, string installRoot) =>
      LspInstallLayout.InstalledEntryPoint(row, installRoot);

  private PrerequisiteProbeResult Probe(LspLanguageRow row) =>
      string.IsNullOrWhiteSpace(row.PrerequisiteExecutable)
          ? PrerequisiteProbeResult.Missing
          : _probe.Probe(row.PrerequisiteExecutable);

  /// <summary>The install command for a row, chosen by the toolchain the row names.</summary>
  private static LspToolchainCommand BuildCommand(LspLanguageRow row, string executable, string prefix)
  {
    string tool = Path.GetFileNameWithoutExtension(executable);
    Dictionary<string, string> environment = [with(StringComparer.OrdinalIgnoreCase)];
    List<string> arguments;
    switch (tool.ToUpperInvariant())
    {
      case "NODE":
        // --global is what puts the launcher at the prefix root; a plain --prefix install
        // nests it in node_modules/.bin, where the row's entry point is not.
        executable = Path.Combine(Path.GetDirectoryName(executable)!, "npm.cmd");
        arguments =
        [
            "install",
            "--global",
            "--prefix",
            prefix,
            "--no-audit",
            "--no-fund",
            row.PackageId + "@" + row.Version,
        ];
        arguments.AddRange(row.Companions.Select(companion => companion.Spec));
        break;
      case "GEM":
        arguments =
        [
            "install",
            row.PackageId,
            "--version",
            row.Version,
            "--install-dir",
            prefix,
            "--bindir",
            prefix,
            "--no-document",
        ];
        break;
      case "GO":
        arguments = ["install", row.PackageId + "@" + row.Version];
        environment["GOBIN"] = prefix;
        break;
      default:
        throw new ArgumentException("Unsupported toolchain: " + tool, nameof(row));
    }

    return new LspToolchainCommand(executable, arguments, environment);
  }

  private static async Task<int> RunToExitAsync(LspToolchainCommand command, CancellationToken cancellationToken)
  {
    ProcessStartInfo startInfo = new(command.Executable)
    {
      CreateNoWindow = true,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    foreach (string argument in command.Arguments)
    {
      startInfo.ArgumentList.Add(argument);
    }

    foreach ((string key, string value) in command.Environment)
    {
      startInfo.Environment[key] = value;
    }

    using Process process = new() { StartInfo = startInfo };
    if (!process.Start())
    {
      return -1;
    }

    Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
    Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    _ = await output.ConfigureAwait(false);
    _ = await error.ConfigureAwait(false);
    return process.ExitCode;
  }
}
