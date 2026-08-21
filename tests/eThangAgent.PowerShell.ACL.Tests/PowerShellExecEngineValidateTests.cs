using eThangAgent.CapabilityDomain;
using eThangAgent.PowerShell.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.PowerShell.ACL.Tests;

public class PowerShellExecEngineValidateTests
{
    private static PowerShellExecEngine CreateEngine()
        => new(CapabilityRegistry.Create(
            [new AgentToolsProvider("agent",
                [new AgentToolBinding(
                    new ReadTool(new FakeFileSystemAccess()), "Read lines.")])]),
            ExecOptions.Default);

    private sealed class FakeFileSystemAccess : IFileSystemAccess
    {
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
            CancellationToken ct = default)
            => Task.FromResult(Result<FileRead>.Success(new FileRead(["alpha", "beta"], 2, 2)));
    }

    [Fact]
    public async Task ValidProgram_ReturnsNoErrors()
    {
        var engine = CreateEngine();

        var result = await engine.ValidateAsync(
            new ExecProgram("Write-Output 'hello'"));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task BrokenSyntax_ReturnsErrors_WithLineAndColumn()
    {
        var engine = CreateEngine();

        var result = await engine.ValidateAsync(
            new ExecProgram("$x = 1\nif ($x {\n"));

        Assert.True(result.IsSuccess);
        var errors = result.Value!;
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.True(e.Line >= 1));
        Assert.All(errors, e => Assert.True(e.Column >= 1));
        Assert.All(errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
    }
}
