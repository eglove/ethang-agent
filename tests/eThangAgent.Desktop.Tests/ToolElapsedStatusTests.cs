using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Status-bar tool elapsed display: BeginTool names the running tool,
///     EndTool freezes the final elapsed with an error marker on failures, Ready
///     clears it, and Tick refreshes the live elapsed. Tests drive a fake seconds
///     clock - no sleeps, exact strings, deterministic.</summary>
public class ToolElapsedStatusTests
{
  [Fact]
  public void BeginTool_Shows_Tool_Name_At_Zero_Elapsed()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default");
    Assert.Equal("", s.ToolDisplay);

    s.BeginTool("read");

    Assert.Equal("read 0.0s", s.ToolDisplay);
  }

  [Fact]
  public void Tick_While_Tool_Runs_Advances_Elapsed_Display()
  {
    double now = 0;
    StatusViewModel s = new("OpenRouter", "m", "Model default", () => now) { Phase = TurnPhase.Thinking }; // tools only run mid-turn; Tick is busy-phase work
    s.BeginTool("read");

    now = 0.8;
    s.Tick();

    Assert.Equal("read 0.8s", s.ToolDisplay);
  }

  [Fact]
  public void EndTool_Freezes_Final_Elapsed_Against_Later_Ticks()
  {
    double now = 0;
    StatusViewModel s = new("OpenRouter", "m", "Model default", () => now);
    s.BeginTool("read");
    now = 2.5;
    s.EndTool(isError: false);
    Assert.Equal("read 2.5s", s.ToolDisplay);

    now = 9;
    s.Tick();

    Assert.Equal("read 2.5s", s.ToolDisplay);
  }

  [Fact]
  public void EndTool_With_Error_Appends_Error_Marker()
  {
    double now = 0;
    StatusViewModel s = new("OpenRouter", "m", "Model default", () => now);
    s.BeginTool("bash");
    now = 1.5;

    s.EndTool(isError: true);

    Assert.Equal("bash 1.5s \u2717", s.ToolDisplay);
  }

  [Fact]
  public void Elapsed_At_Or_Above_A_Minute_Formats_As_m_ss()
  {
    double now = 0;
    StatusViewModel s = new("OpenRouter", "m", "Model default", () => now);
    s.BeginTool("exec");
    now = 125;

    s.EndTool(isError: false);

    Assert.Equal("exec 2:05", s.ToolDisplay);
  }

  [Fact]
  public void Ready_Clears_Tool_Display()
  {
    double now = 0;
    StatusViewModel s = new("OpenRouter", "m", "Model default", () => now);
    s.BeginTool("read");
    s.EndTool(isError: false);
    Assert.NotEqual("", s.ToolDisplay);

    s.Phase = TurnPhase.Ready;

    Assert.Equal("", s.ToolDisplay);
  }

  [Fact]
  public void BeginTool_After_EndTool_Starts_A_Fresh_Timing()
  {
    double now = 0;
    StatusViewModel s = new("OpenRouter", "m", "Model default", () => now);
    s.BeginTool("read");
    s.EndTool(isError: true);

    s.BeginTool("bash");

    Assert.Equal("bash 0.0s", s.ToolDisplay);
  }

  [Fact]
  public void EndTool_Without_Begin_Is_A_Safe_NoOp()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default");

    s.EndTool(isError: false);

    Assert.Equal("", s.ToolDisplay);
  }
}
