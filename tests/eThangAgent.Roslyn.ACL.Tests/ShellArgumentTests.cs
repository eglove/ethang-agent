using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Shell() argument contract (issue #95): every argument after the
/// executable is ONE native argument, passed to the process verbatim through
/// ProcessStartInfo.ArgumentList. An argument containing spaces — a path, a
/// commit message — reaches the target process as a single argument; the
/// joined line is never re-split.</summary>
public class ShellArgumentTests
{
  private readonly CSharpScriptExecEngine _engine =
      new(CapabilityRegistry.Create([]),
          workspaceRoot: () => AppContext.BaseDirectory);

  [Fact]
  public void SpacedExecutablePath_IsOneNativeToken()
  {
    string cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    DirectoryInfo tmp = Directory.CreateTempSubdirectory("shellarg spaced exe");
    try
    {
      string exe = Path.Combine(tmp.FullName, "my spaced tool.exe");
      File.Copy(cmdPath, exe);

      ScriptGlobals globals = new(NoopRegistry(), tmp.FullName, tmp.FullName);
      ShellResult result = globals.Shell(exe, "/c", "exit 0");

      Assert.True(result.ExitCode == 0,
          $"expected exit 0 from a spaced executable path; got {result.ExitCode}: {result.Stderr}");
    }
    finally
    {
      Cleanup(tmp);
    }
  }

  [Fact]
  public async Task SpacedArgument_ReachesGitAsOneArgument()
  {
    DirectoryInfo tmp = Directory.CreateTempSubdirectory("shellarg-spaced-msg");
    try
    {
      string dir = tmp.FullName.Replace("\\", "/", StringComparison.Ordinal);

      ExecRunResult init = await _engine.ExecuteAsync(new ExecProgram(
          $"var r = Shell(\"git\", \"-C\", \"{dir}\", \"init\"); return r.ExitCode.ToString();"),
          ct: TestContext.Current.CancellationToken);
      Assert.True(init.Output.Trim() == "0",
          $"git init failed: {init.Output} {string.Join(';', init.ErrorLines)}");

      // The commit message "two words" is ONE Shell argument; git must receive
      // it as one argv entry, not as the message "two" plus a "words" pathspec.
      ExecRunResult commit = await _engine.ExecuteAsync(new ExecProgram(
          $"var r = Shell(\"git\", \"-C\", \"{dir}\", \"-c\", \"user.email=t@t\", \"-c\", \"user.name=t\", " +
          $"\"commit\", \"--allow-empty\", \"-m\", \"two words\"); return r.ExitCode.ToString();"),
          ct: TestContext.Current.CancellationToken);
      Assert.True(commit.Output.Trim() == "0",
          $"expected exit 0 from git commit with a spaced -m message; got: " +
          $"{commit.Output} {string.Join(';', commit.ErrorLines)}");

      ExecRunResult log = await _engine.ExecuteAsync(new ExecProgram(
          $"var r = Shell(\"git\", \"-C\", \"{dir}\", \"log\", \"-1\", \"--format=%s\"); return r.Stdout;"),
          ct: TestContext.Current.CancellationToken);
      Assert.Equal("two words", log.Output.Trim());
    }
    finally
    {
      Cleanup(tmp);
    }
  }

  [Fact]
  public void VerificationRecord_TokensAreVerbatimArguments()
  {
    RecordingSink sink = new();
    ScriptGlobals globals = new(NoopRegistry(), Path.GetTempPath(), Path.GetTempPath(),
        verificationSink: sink);

    _ = globals.Shell("cmd", "/c", "echo", "hello world");

    ShellExecutionRecord record = Assert.Single(sink.Records);
    Assert.Equal((string[])["cmd", "/c", "echo", "hello world"], record.Tokens);
  }

  private static void Cleanup(DirectoryInfo tmp)
  {
    // git marks its object files read-only; clear attributes before deleting.
    try
    {
      foreach (string f in Directory.EnumerateFiles(tmp.FullName, "*", SearchOption.AllDirectories))
      {
        File.SetAttributes(f, FileAttributes.Normal);
      }

      tmp.Delete(recursive: true);
    }
    // Named decision (CA1031): temp-dir cleanup is best effort.
#pragma warning disable CA1031 // Do not catch general exception types
    catch { /* best effort */ }
#pragma warning restore CA1031
  }

  // Shell never resolves tools or invokes capabilities; an empty registry stub
  // satisfies ScriptTools' non-null contract.
  private static EmptyRegistry NoopRegistry() => new();

  private sealed class EmptyRegistry : ICapabilityRegistry
  {
    public Result<ResolvedCapability> Resolve(string nameOrRef) =>
        Result.Failure<ResolvedCapability>(new DomainError("NotFound", "unused"));

    public IReadOnlyList<ProviderCapabilities> Providers => [];

    public Task<CapabilityInvocationResult> InvokeAsync(
        ResolvedCapability capability, string jsonArguments, CancellationToken ct = default) =>
        throw new NotSupportedException("unused");
  }

  private sealed class RecordingSink : IVerificationLedger
  {
    public List<ShellExecutionRecord> Records { get; } = [];

    public void Append(ShellExecutionRecord record) => Records.Add(record);

    public IReadOnlyList<ShellExecutionRecord> Snapshot() => [.. Records];
  }
}
