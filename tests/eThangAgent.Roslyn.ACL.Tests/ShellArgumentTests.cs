using eThangAgent.CapabilityDomain;
using eThangAgent.Roslyn.ACL;
using eThangAgent.ToolDomain;
using Xunit;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Shell() argument contract: each argument after the executable is one
/// token of a single native command line; a multi-token piece passed as one argument
/// is re-parsed as tokens instead of being quoted as a single literal. Pins the
/// real-use failure where Shell("dotnet", "build -c Release") reached dotnet as one
/// literal argument.</summary>
public class ShellArgumentTests
{
    private readonly CSharpScriptExecEngine _engine =
        new(CapabilityRegistry.Create([]), ExecOptions.Default,
            workspaceRoot: () => AppContext.BaseDirectory);

    [Fact]
    public async Task MultiTokenArguments_ArePassedAsSeparateTokens()
    {
        var run = await _engine.ExecuteAsync(new ExecProgram(
            "var r = Shell(\"cmd\", \"/c\", \"echo\", \"hello world\"); return r.Stdout;"));
        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Contains("hello world", run.Output);
    }

    [Fact]
    public async Task GitStatusShort_ParsesMultiTokenFlag()
    {
        var run = await _engine.ExecuteAsync(new ExecProgram(
            "var r = Shell(\"git\", \"status\", \"--short\"); return r.ExitCode.ToString();"));
        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Equal("0", run.Output.Trim());
    }

    [Fact]
    public async Task WholeCommandLineAsSingleArgument_IsReparsedAsTokens()
    {
        // Regression pin: the second Shell argument below is ONE string holding several
        // tokens plus a quoted path; git must receive them as separate argv entries.
        var tmp = Directory.CreateTempSubdirectory("shellarg-repro");
        try
        {
            var dir = tmp.FullName.Replace("\\", "/");

            var initScript =
                $"var r = Shell(\"git\", \"init \\\"{dir}\\\"\"); return r.ExitCode.ToString();";
            var init = await _engine.ExecuteAsync(new ExecProgram(initScript));
            Assert.True(init.Output.Trim() == "0",
                $"git init failed: {init.Output} {string.Join(';', init.ErrorLines)}");

            var commitScript =
                $"var r = Shell(\"git\", \"-c user.email=t@t -c user.name=t -C \\\"{dir}\\\"" +
                $" commit --allow-empty -m x\"); return r.ExitCode.ToString();";
            var commit = await _engine.ExecuteAsync(new ExecProgram(commitScript));
            Assert.True(commit.Output.Trim() == "0",
                $"expected exit 0 from git invoked with a multi-token single argument; got: " +
                $"{commit.Output} {string.Join(';', commit.ErrorLines)}");
        }
        finally
        {
            // git marks its object files read-only; clear attributes before deleting.
            try
            {
                foreach (var f in Directory.EnumerateFiles(tmp.FullName, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                tmp.Delete(recursive: true);
            }
            catch { /* best effort */ }
        }
    }
}