using eThangAgent.Desktop;

namespace eThangAgent.Desktop.Tests;

/// <summary>Drives the workspace-startup decision loop with faked delegates — no Avalonia
/// dialogs involved. The loop must re-prompt until a directory is chosen or the user exits.</summary>
public class WorkspaceStartupFlowTests
{
    private static Task<string?> Picked(string? path) => Task.FromResult(path);

    [Fact]
    public async Task First_Pick_Is_Accepted_Without_A_Required_Dialog()
    {
        var flow = new WorkspaceStartupFlow();
        var picks = 0;

        var result = await flow.RunAsync(
            () => { picks++; return Picked(@"C:\some\root"); },
            () => throw new InvalidOperationException("required dialog must not appear"));

        Assert.False(result.ExitRequested);
        Assert.Equal(@"C:\some\root", result.Root);
        Assert.Equal(1, picks);
    }

    [Fact]
    public async Task Cancel_Then_Pick_Reprompts_And_Accepts_Second_Choice()
    {
        var flow = new WorkspaceStartupFlow();
        var queue = new Queue<string?>([null, @"C:\second\root"]);
        var requiredShown = 0;

        var result = await flow.RunAsync(
            () => Picked(queue.Dequeue()),
            () => { requiredShown++; return Task.FromResult(true); });

        Assert.False(result.ExitRequested);
        Assert.Equal(@"C:\second\root", result.Root);
        Assert.Equal(1, requiredShown);
    }

    [Fact]
    public async Task Repeated_Cancels_Reprompt_Every_Time_Until_A_Pick()
    {
        var flow = new WorkspaceStartupFlow();
        var queue = new Queue<string?>([null, null, null, @"C:\fourth"]);
        var requiredShown = 0;

        var result = await flow.RunAsync(
            () => Picked(queue.Dequeue()),
            () => { requiredShown++; return Task.FromResult(true); });

        Assert.Equal(@"C:\fourth", result.Root);
        Assert.Equal(3, requiredShown);
    }

    [Fact]
    public async Task Declining_The_Required_Dialog_Exits_Without_A_Root()
    {
        var flow = new WorkspaceStartupFlow();

        var result = await flow.RunAsync(
            () => Picked(null),
            () => Task.FromResult(false));

        Assert.True(result.ExitRequested);
        Assert.Null(result.Root);
    }
}