using eThangAgent.Terminal.ACL;

namespace eThangAgent.Terminal.ACL.Tests;

public class ConsoleSpinnerTests
{
    [Fact]
    public async Task AlreadyCompletedTask_ProducesNoOutput()
    {
        var writer = new FakeWriter();
        var spinner = new ConsoleSpinner(writer, intervalMs: 1);

        await spinner.RunWhile(Task.CompletedTask, "Thinking");

        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task PendingTask_AnimatesFrames_ThenClearsLine()
    {
        var writer = new FakeWriter();
        var spinner = new ConsoleSpinner(writer, intervalMs: 5);
        var tcs = new TaskCompletionSource();

        var run = spinner.RunWhile(tcs.Task, "Thinking");
        await Task.Delay(60);
        tcs.SetResult();
        await run;

        Assert.Contains(writer.Writes, w => w.Text.Contains("Thinking"));
        Assert.Contains(writer.Writes, w => w.Text.Contains('\r'));
        var last = writer.Writes[^1].Text;
        Assert.True(last.Trim().Length == 0 || last.EndsWith('\r'), $"expected line clear, got: {last}");
    }

    [Fact]
    public async Task RunWhile_CompletesOnlyAfterTaskCompletes()
    {
        var writer = new FakeWriter();
        var spinner = new ConsoleSpinner(writer, intervalMs: 5);
        var tcs = new TaskCompletionSource();

        var run = spinner.RunWhile(tcs.Task, "Thinking");
        await Task.Delay(30);

        Assert.False(run.IsCompleted);
        tcs.SetResult();
        await run;
        Assert.True(run.IsCompletedSuccessfully);
    }
}
