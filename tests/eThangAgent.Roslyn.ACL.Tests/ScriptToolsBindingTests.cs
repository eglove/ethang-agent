using eThangAgent.CapabilityDomain;
using eThangAgent.Roslyn.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Xunit;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Script-tools binding contract: zero-argument actions must bind WITHOUT a
/// dummy argument object (only timeoutSeconds is mandatory on every call). Pins the
/// real-use failure where Tools.git_status() failed with 'no argument given for
/// required parameter' while Tools.Invoke("git_status", new { }) worked.</summary>
public class ScriptToolsBindingTests
{
    private sealed class StubProvider : ICapabilityProvider
    {
        public string Id => "stub";
        public IReadOnlyList<ActionDescriptor> Actions { get; } =
        [
            new ActionDescriptor("git_status", "Show branch status.",
                "Returns OK.", []),
        ];
        public Task<CapabilityInvocationResult> InvokeAsync(string actionName,
            string jsonArguments, CancellationToken ct = default) =>
            Task.FromResult(CapabilityInvocationResult.Ok("ok"));
    }

    private static CSharpScriptExecEngine MakeEngine() =>
        new(CapabilityRegistry.Create([new StubProvider()]), ExecOptions.Default,
            workspaceRoot: () => AppContext.BaseDirectory);

    [Fact]
    public async Task ParameterlessAction_BindsWithZeroArguments()
    {
        // Source-level call exactly as a script writes it — the Roslyn binder must
        // apply the parameter's default.
        var run = await MakeEngine().ExecuteAsync(new ExecProgram(
            "return Tools.git_status(new { timeoutSeconds = 30 });"));
        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Empty(run.ErrorLines);
        Assert.Equal("ok", run.Output.Trim());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("search_files")]
    [InlineData("exec")]
    [InlineData("git_status")]
    [InlineData("working_diff")]
    [InlineData("git_commit")]
    public void EveryDeclaredConvenienceMethod_HasOnlyOptionalParameters(string name)
    {
        var method = typeof(ScriptTools).GetMethod(name);
        Assert.NotNull(method);
        Assert.All(method!.GetParameters(), p => Assert.True(p.HasDefaultValue,
            $"{name} declares required parameter '{p.Name}'; scripts cannot call it bare."));
    }

    [Fact]
    public async Task GenericInvoke_WithNullArgs_EqualsBareCall()
    {
        var engine = MakeEngine();
        var bare = await engine.ExecuteAsync(new ExecProgram(
            "return Tools.git_status(new { timeoutSeconds = 30 });"));
        var generic = await engine.ExecuteAsync(new ExecProgram(
            "return Tools.Invoke(\"git_status\", new { timeoutSeconds = 30 });"));
        Assert.Equal(bare.Output.Trim(), generic.Output.Trim());
        Assert.Equal("ok", generic.Output.Trim());
    }
}