using eThangAgent.Desktop;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>The clarify channel without a presenter reports PresenterUnavailable — a
/// structured, model-actionable failure — instead of throwing. Late-binding a presenter
/// makes it answer normally.</summary>
public class AvaloniaClarifyChannelTests
{
    [Fact]
    public async Task WithoutPresenter_FailsWithPresenterUnavailable()
    {
        var channel = new AvaloniaClarifyChannel(null);

        var result = await channel.AskAsync(new ClarifyQuestion("Which?", ["a", "b"], true));

        Assert.False(result.IsSuccess);
        Assert.Equal("PresenterUnavailable", result.Error!.Code);
        Assert.Contains("directly in chat", result.Error!.Message);
    }

    [Fact]
    public async Task LateBoundPresenter_AnswersNormally()
    {
        var channel = new AvaloniaClarifyChannel(null);

        var question = new ClarifyQuestion("Which?", ["a", "b"], false);
        var vm = new ClarifyViewModel(question);
        channel.SetPresenter(q => { vm.ChooseOption(2); return Task.FromResult(vm); });

        var result = await channel.AskAsync(question);

        Assert.True(result.IsSuccess);
        Assert.Equal("2", result.Value); // channel returns the raw selection; the tool maps it to text
    }
}