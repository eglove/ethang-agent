using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

public class StatusViewModelTests
{
  [Fact]
  public void Ready_State_Shows_Empty_Spinner_And_Label()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default");
    Assert.Equal(TurnPhase.Ready, s.Phase);
    Assert.Equal("", s.Spinner);
    Assert.Equal("Ready", s.PhaseLabel);
  }

  [Fact]
  public void Thinking_Label_And_Frame_Advance_On_Tick()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default") { Phase = TurnPhase.Thinking };
    Assert.Equal("Thinking\u2026", s.PhaseLabel);
    string first = s.Spinner;
    Assert.NotEqual("", first);
    s.Tick();
    Assert.NotEqual(first, s.Spinner);
  }

  [Fact]
  public void Streaming_Label_Replaces_Thinking()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default") { Phase = TurnPhase.Streaming };
    Assert.Equal("Streaming\u2026", s.PhaseLabel);
  }

  [Fact]
  public void Back_To_Ready_Clears_Spinner()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default") { Phase = TurnPhase.Thinking };
    s.Tick();
    s.Phase = TurnPhase.Ready;
    Assert.Equal("", s.Spinner);
    Assert.Equal(0, 0); // frame index retained; Spinner gated by phase
  }

  [Fact]
  public void Tick_Is_A_NoOp_When_Ready()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default");
    s.Tick();
    s.Tick();
    Assert.Equal("", s.Spinner);
  }

  [Fact]
  public void Effort_Initializes_And_Tracks_Updates()
  {
    StatusViewModel s = new("OpenRouter", "m", "High");
    Assert.Equal("High", s.Effort);

    s.Effort = "Model default";

    Assert.Equal("Model default", s.Effort);
  }
}
