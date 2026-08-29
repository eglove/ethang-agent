using eThangAgent.CapabilityDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Shell() argument contract: each argument after the executable is one
/// token of a single native command line; a multi-token piece passed as one argument
/// is re-parsed as tokens instead of being quoted as a single literal. Pins the
/// real-use failure where Shell("dotnet", "build -c Release") reached dotnet as one
/// literal argument.</summary>
public class ShellArgumentTests
{
  private readonly CSharpScriptExecEngine _engine =
      new(CapabilityRegistry.Create([]),
          workspaceRoot: () => AppContext.BaseDirectory);

  [Fact]
  public async Task MultiTokenArguments_ArePassedAsSeparateTokens()
  {
    ExecRunResult run = await _engine.ExecuteAsync(new ExecProgram(
        "var r = Shell(\"cmd\", \"/c\", \"echo\", \"hello world\"); return r.Stdout;"), ct: TestContext.Current.CancellationToken);
    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Contains("hello world", run.Output, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GitStatusShort_ParsesMultiTokenFlag()
  {
    ExecRunResult run = await _engine.ExecuteAsync(new ExecProgram(
        "var r = Shell(\"git\", \"status\", \"--short\"); return r.ExitCode.ToString();"), ct: TestContext.Current.CancellationToken);
    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Equal("0", run.Output.Trim());
  }

  [Fact]
  public async Task WholeCommandLineAsSingleArgument_IsReparsedAsTokens()
  {
    // Regression pin: the second Shell argument below is ONE string holding several
    // tokens plus a quoted path; git must receive them as separate argv entries.
    DirectoryInfo tmp = Directory.CreateTempSubdirectory("shellarg-repro");
    try
    {
      string dir = tmp.FullName.Replace("\\", "/", StringComparison.Ordinal);

      string initScript =
          $"var r = Shell(\"git\", \"init \\\"{dir}\\\"\"); return r.ExitCode.ToString();";
      ExecRunResult init = await _engine.ExecuteAsync(new ExecProgram(initScript), ct: TestContext.Current.CancellationToken);
      Assert.True(init.Output.Trim() == "0",
          $"git init failed: {init.Output} {string.Join(';', init.ErrorLines)}");

      string commitScript =
          $"var r = Shell(\"git\", \"-c user.email=t@t -c user.name=t -C \\\"{dir}\\\"" +
          $" commit --allow-empty -m x\"); return r.ExitCode.ToString();";
      ExecRunResult commit = await _engine.ExecuteAsync(new ExecProgram(commitScript), ct: TestContext.Current.CancellationToken);
      Assert.True(commit.Output.Trim() == "0",
          $"expected exit 0 from git invoked with a multi-token single argument; got: " +
          $"{commit.Output} {string.Join(';', commit.ErrorLines)}");
    }
    finally
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
  }
}
