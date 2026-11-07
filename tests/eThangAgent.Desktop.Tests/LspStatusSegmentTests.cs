using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Lsp.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>The status bar's language-server segment: the user must be able to tell a
///     one-time download from a hung app, and must not be told anything at all while the
///     daemon has announced nothing.</summary>
[Trait("Category", "Process")]
public class LspStatusSegmentTests
{
  [Fact]
  public void BeforeAnyAnnouncement_TheSegmentIsEmpty()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    Assert.Equal("", status.ServerDisplay);
    Assert.Equal("", status.ServerBreakdownText);
  }

  [Fact]
  public void ADownload_SaysWhatIsHappeningAndHowBigItIs()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    status.SetServerStatus(LspServerInstallState.Downloading, string.Empty);

    Assert.Equal("C# server: downloading (64 MB)", status.ServerDisplay);
  }

  [Fact]
  public void Ready_NamesTheVersionItIsServing()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    status.SetServerStatus(LspServerInstallState.Ready, "5.13.0-1.26514.10");

    Assert.Equal("C# server: ready 5.13.0-1.26514.10", status.ServerDisplay);
  }

  [Fact]
  public void Unavailable_SaysSoWithoutInventingAReason()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    status.SetServerStatus(LspServerInstallState.Unavailable, string.Empty);

    Assert.Equal("C# server: unavailable", status.ServerDisplay);
  }

  [Fact]
  public void GoingBackToUnknown_ClearsTheSegment()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");
    status.SetServerStatus(LspServerInstallState.Ready, "1.0.0");

    status.SetServerStatus(LspServerInstallState.Unknown, string.Empty);

    Assert.Equal("", status.ServerDisplay);
  }

  [AvaloniaFact]
  public void EveryStateChange_RaisesTheSegment()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");
    List<string?> raised = [];
    status.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

    status.SetServerStatus(LspServerInstallState.Downloading, string.Empty);

    Assert.Contains(nameof(StatusViewModel.ServerDisplay), raised);
  }
}
