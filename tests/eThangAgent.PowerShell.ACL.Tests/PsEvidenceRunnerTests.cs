using eThangAgent.StateDomain;
using eThangAgent.PowerShell.ACL;

namespace eThangAgent.PowerShell.ACL.Tests;

public class PsEvidenceRunnerTests
{
    private readonly PsEvidenceRunner _runner =
        new(new EvidenceOptions { Timeout = TimeSpan.FromSeconds(10) });

    [Fact]
    public async Task ConfirmingCommand_IsConfirmed()
    {
        var result = await _runner.RunAsync("Write-Output ok");
        Assert.True(result.Confirmed);
    }

    [Fact]
    public async Task WriteError_IsNotConfirmed()
    {
        var result = await _runner.RunAsync("Write-Error boom");
        Assert.False(result.Confirmed);
        Assert.Contains("boom", result.Detail);
    }

    [Fact]
    public async Task NativeExitCodeOne_IsNotConfirmed()
    {
        var result = await _runner.RunAsync("cmd /c exit 1");
        Assert.False(result.Confirmed);
        Assert.Contains("LASTEXITCODE", result.Detail);
    }

    [Fact]
    public async Task SyntaxError_FailsClosed()
    {
        var result = await _runner.RunAsync("if (x {");
        Assert.False(result.Confirmed);
    }

    [Fact]
    public async Task Timeout_FailsClosed_WithDetail()
    {
        var runner = new PsEvidenceRunner(new EvidenceOptions { Timeout = TimeSpan.FromMilliseconds(300) });

        var result = await runner.RunAsync("Start-Sleep -Seconds 300");

        Assert.False(result.Confirmed);
        Assert.Contains("Timed out", result.Detail);
    }
}
