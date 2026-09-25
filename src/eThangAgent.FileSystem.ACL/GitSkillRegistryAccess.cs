using System.Diagnostics;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

// Named decisions: git child processes are short-lived and awaited; sync
// directory IO keeps the adapter mechanical (the GitWorktreeAccess pattern).
#pragma warning disable CA1849 // Call async methods when in an async method

namespace eThangAgent.FileSystem.ACL;

/// <summary>Filesystem + git adapter for <see cref="ISkillRegistryAccess"/>
/// (plan #29 task 7): shallow clone via the git CLI resolved once through an
/// absolute path (S4036), typed DomainErrors carrying git's stderr tail, and
/// mechanical folder copy/remove. A URL carrying user information is refused
/// BEFORE any git invocation (public HTTPS only, spec #27 out-of-scope
/// decision); removing a configured directory root itself is refused — the
/// service guarantees paths are skill folders; the adapter refuses drive
/// roots as a last-resort mechanical guard.</summary>
public sealed class GitSkillRegistryAccess : ISkillRegistryAccess
{
  private static readonly Lazy<string> GitExePath = new(ResolveGitExecutable);

  public Task<Result<string>> CloneAsync(CloneRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (!string.IsNullOrEmpty(request.CloneUrl.UserInfo))
    {
      return Task.FromResult(Result.Failure<string>(new DomainError("CredentialsRejected",
          "Cloning from URLs with embedded credentials is not supported (public HTTPS only).")));
    }

    string? parent = Path.GetDirectoryName(request.StagingRoot);
    if (string.IsNullOrEmpty(parent))
    {
      return Task.FromResult(Result.Failure<string>(new DomainError("CloneFailed", $"Invalid staging path: {request.StagingRoot}")));
    }

    _ = Directory.CreateDirectory(parent);
    ProcessStartInfo psi = new(GitExePath.Value)
    {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    psi.ArgumentList.Add("clone");
    psi.ArgumentList.Add("--depth");
    psi.ArgumentList.Add("1");
    psi.ArgumentList.Add(request.CloneUrl.ToString());
    psi.ArgumentList.Add(request.StagingRoot);

    using Process p = Process.Start(psi)!;
    _ = p.StandardOutput.ReadToEnd();
    string stderr = p.StandardError.ReadToEnd();
    _ = p.WaitForExit(120000);
    return Task.FromResult(p.ExitCode == 0
        ? Result.Success(request.StagingRoot)
        : Result.Failure<string>(new DomainError("CloneFailed", $"git clone failed (exit {p.ExitCode}): {Tail(stderr)}")));
  }

  public Task<Result<string>> PromoteAsync(PromoteRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (!Directory.Exists(request.SourcePath))
    {
      return Task.FromResult(Result.Failure<string>(new DomainError("TargetMissing", $"Staged skill folder does not exist: {request.SourcePath}")));
    }

    if (!Directory.Exists(request.TargetDirectory))
    {
      return Task.FromResult(Result.Failure<string>(new DomainError("TargetMissing", $"Target directory does not exist: {request.TargetDirectory}")));
    }

    string destination = Path.Combine(request.TargetDirectory, Path.GetFileName(request.SourcePath));
    if (Directory.Exists(destination) && !request.Force)
    {
      return Task.FromResult(Result.Failure<string>(new DomainError("DestinationExists", $"A skill folder already exists at {destination}; retry with force to replace it.")));
    }

    if (Directory.Exists(destination))
    {
      Directory.Delete(destination, true);
    }

    CopyAll(request.SourcePath, destination);
    return Task.FromResult(Result.Success(destination));
  }

  public Task<Result<string>> RemoveAsync(string skillDirectory, CancellationToken ct = default)
  {
    if (!Directory.Exists(skillDirectory))
    {
      return Task.FromResult(Result.Failure<string>(new DomainError("SkillNotFound", $"No skill folder at {skillDirectory}.")));
    }

    string full = Path.GetFullPath(skillDirectory);
    if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
    {
      return Task.FromResult(Result.Failure<string>(new DomainError("ConfiguredRootRefused", $"Refusing to remove a drive root: {skillDirectory}")));
    }

    Directory.Delete(skillDirectory, true);
    return Task.FromResult(Result.Success(skillDirectory));
  }

  private static void CopyAll(string source, string destination)
  {
    foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
    {
      _ = Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
    }

    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
      string target = Path.Combine(destination, Path.GetRelativePath(source, file));
      _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.Copy(file, target, true);
    }

    foreach (string topFile in Directory.EnumerateFiles(source))
    {
      string target = Path.Combine(destination, Path.GetFileName(topFile));
      File.Copy(topFile, target, true);
    }
  }

  private static string Tail(string stderr)
  {
    string trimmed = stderr.Trim();
    return trimmed.Length <= 400 ? trimmed : trimmed[^400..];
  }

  private static string ResolveGitExecutable()
  {
    string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    string[] candidates =
    [
      Path.Combine(programFiles, "Git", "cmd", "git.exe"),
      Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe"),
    ];

    return candidates.FirstOrDefault(File.Exists) ?? "git";
  }
}
