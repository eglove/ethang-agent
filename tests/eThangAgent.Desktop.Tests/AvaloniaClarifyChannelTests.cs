using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
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
    AvaloniaClarifyChannel channel = new(null);

    Result<string> result = await channel.AskAsync(new ClarifyQuestion("Which?", ["a", "b"], true));

    Assert.False(result.IsSuccess);
    Assert.Equal("PresenterUnavailable", result.Error!.Code);
    Assert.Contains("directly in chat", result.Error!.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LateBoundPresenter_AnswersNormally()
  {
    AvaloniaClarifyChannel channel = new(null);

    ClarifyQuestion question = new("Which?", ["a", "b"], false);
    ClarifyViewModel vm = new(question);
    channel.SetPresenter(q =>
    {
      vm.ChooseOption(2);
      return Task.FromResult(vm);
    });

    Result<string> result = await channel.AskAsync(question);

    Assert.True(result.IsSuccess);
    Assert.Equal("2", result.Value); // channel returns the raw selection; the tool maps it to text
  }
}
