
namespace eThangAgent.ToolDomain.Tests;

/// <summary>Contract tests for the Computer Use Tool Domain layer (tasks 9-11):
///     record validation on the seam records (exactly-one app_ref, non-negative
///     targets, outcome construction) per the task-9 brief, plus later-task
///     additions parser-side.</summary>
public class ComputerRecordsTests
{
  private static ToolResultImage Shot() => new("image/png", "aGVsbG8=");

  private static ComputerElement El(int index) => new(index, "button", "control", "OK", null,
      [0, 0, 40, 20], Enabled: true, Editable: false, Actions: ["press"],
      Focused: false, Selected: false, Pressable: true, HasMenu: false,
      ChildrenTotal: null, ChildrenShown: null, ChildrenOffset: null);

  private static ComputerFrameRef Frame() => new("frame-abc12345", 640, 480);

  private static ComputerOutcome.Observation Observation(
      string? withheld = null, ToolResultImage? screenshot = null) => new(
      "tree text", "s-1", [El(1)], Frame(), withheld, screenshot);

  private static ComputerOutcome.Receipt Receipt(bool sent = true,
      string? evidence = null, string? tree = null) => new(sent, "accepted", evidence, tree);

  private static readonly string[] KnownHints = ["retry", "reobserve", "never"];

  private static string[] Codes() =>
  [
    ComputerErrorCodes.AppNotFound, ComputerErrorCodes.AmbiguousApp, ComputerErrorCodes.ElementUnavailable,
    ComputerErrorCodes.StaleState, ComputerErrorCodes.NotSettable, ComputerErrorCodes.NotSelectable,
    ComputerErrorCodes.ActionUnavailable, ComputerErrorCodes.ForegroundRequired, ComputerErrorCodes.ControllerBusy,
    ComputerErrorCodes.ControlStopped, ComputerErrorCodes.HelperUnavailable, ComputerErrorCodes.VersionMismatch,
    ComputerErrorCodes.Timeout, ComputerErrorCodes.InvalidApp, ComputerErrorCodes.LaunchFailed,
    ComputerErrorCodes.Internal,
  ];

  // ---- ComputerAppRef: exactly-one-of ----

  [Fact]
  public void AppRef_ByPid_CarriesPid()
  {
    ComputerAppRef app = ComputerAppRef.ByPid(4242);
    Assert.Equal(4242, app.Pid);
    Assert.Null(app.Name);
    Assert.Null(app.Aumid);
    Assert.Null(app.WindowId);
  }

  [Fact]
  public void AppRef_ByName_CarriesName()
  {
    ComputerAppRef app = ComputerAppRef.ByName("Notepad");
    Assert.Equal("Notepad", app.Name);
    Assert.Null(app.Pid);
    Assert.Null(app.Aumid);
  }

  [Fact]
  public void AppRef_ByAumid_CarriesAumid()
  {
    ComputerAppRef app = ComputerAppRef.ByAumid("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");
    Assert.Equal("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", app.Aumid);
    Assert.Null(app.Name);
    Assert.Null(app.Pid);
  }

  [Fact]
  public void AppRef_WindowId_CanRideOnNameRef()
  {
    ComputerAppRef app = ComputerAppRef.ByName("Notepad", WindowId: 7);
    Assert.Equal(7, app.WindowId);
  }

  [Theory]
  [InlineData(1, 2, 0)]
  [InlineData(1, 2, 3)]
  public void AppRef_TwoOrMoreSelectors_Throws(int name, int pid, int aumid)
  {
    _ = Assert.Throws<ArgumentException>(() => new ComputerAppRef(
        name > 0 ? "n" : null, pid > 0 ? pid : null, aumid > 0 ? "a" : null, null));
  }

  [Fact]
  public void AppRef_ZeroSelectors_Throws() => _ = Assert.Throws<ArgumentException>(() => new ComputerAppRef(null, null, null, null));

  [Fact]
  public void AppRef_ByPid_NonPositivePid_Throws()
  {
    _ = Assert.Throws<ArgumentException>(() => ComputerAppRef.ByPid(0));
    _ = Assert.Throws<ArgumentException>(() => ComputerAppRef.ByPid(-1));
  }

  [Fact]
  public void AppRef_ByName_EmptyName_Throws() => _ = Assert.Throws<ArgumentException>(() => ComputerAppRef.ByName(""));

  [Fact]
  public void AppRef_ByAumid_EmptyAumid_Throws() => _ = Assert.Throws<ArgumentException>(() => ComputerAppRef.ByAumid(""));

  [Fact]
  public void AppRef_NegativeWindowId_Throws() => _ = Assert.Throws<ArgumentException>(() => ComputerAppRef.ByName("Notepad", WindowId: -1));

  // ---- ComputerTarget: nested records, non-negative ----

  [Fact]
  public void Target_Element_CarriesIndex()
  {
    ComputerTarget target = ComputerTarget.Element(3);
    Assert.Equal(3, target.ElementIndex);
    Assert.Null(target.X);
    Assert.Null(target.Y);
  }

  [Fact]
  public void Target_Coordinate_CarriesPoint()
  {
    ComputerTarget target = ComputerTarget.Coordinate(10, 20);
    Assert.Equal(10, target.X);
    Assert.Equal(20, target.Y);
    Assert.Null(target.ElementIndex);
  }

  [Fact]
  public void Target_Element_NegativeIndex_Throws()
  {
    Exception ex = Assert.ThrowsAny<Exception>(() => ComputerTarget.Element(-1));
    _ = Assert.IsType<ArgumentException>(ex, exactMatch: false);
  }

  [Fact]
  public void Target_Coordinate_Negative_Throws()
  {
    Exception x = Assert.ThrowsAny<Exception>(() => ComputerTarget.Coordinate(-1, 0));
    _ = Assert.IsType<ArgumentException>(x, exactMatch: false);
    Exception y = Assert.ThrowsAny<Exception>(() => ComputerTarget.Coordinate(0, -1));
    _ = Assert.IsType<ArgumentException>(y, exactMatch: false);
  }

  // ---- Outcome construction ----

  [Fact]
  public void Observation_CarriesAllFields()
  {
    ComputerOutcome.Observation observation = Observation();
    Assert.Equal("tree text", observation.TreeText);
    Assert.Equal("s-1", observation.StateId);
    _ = Assert.Single(observation.Elements);
    Assert.NotNull(observation.Frame);
    Assert.Null(observation.WithheldReason);
    Assert.Null(observation.Screenshot);
  }

  [Fact]
  public void Observation_WithScreenshot_CarriesImage()
  {
    ToolResultImage screenshot = Shot();
    ComputerOutcome.Observation observation = new(
        "tree", "s-2", [El(1)], null, null, screenshot);
    Assert.Same(screenshot, observation.Screenshot);
  }

  [Fact]
  public void Receipt_CarriesAllFields()
  {
    ComputerOutcome.Receipt receipt = Receipt(sent: false, evidence: "unchanged", tree: "t");
    Assert.False(receipt.ActionSent);
    Assert.Equal("accepted", receipt.DispatchStatus);
    Assert.Equal("unchanged", receipt.EffectEvidence);
    Assert.Equal("t", receipt.TreeText);
  }

  [Fact]
  public void Failure_CarriesCodeAndMessage()
  {
    ComputerOutcome.Failure failure = new(ComputerErrorCodes.Timeout, "the request took too long");
    Assert.Equal(ComputerErrorCodes.Timeout, failure.Code);
    Assert.Equal("the request took too long", failure.Message);
  }

  // ---- Error codes + retry hints ----

  [Fact]
  public void ErrorCodes_AreTheSpecVerbatimStrings()
  {
    string[] expected =
    [
      "APP_NOT_FOUND", "AMBIGUOUS_APP", "ELEMENT_UNAVAILABLE", "STALE_STATE",
      "NOT_SETTABLE", "NOT_SELECTABLE", "ACTION_UNAVAILABLE", "FOREGROUND_REQUIRED",
      "CONTROLLER_BUSY", "CONTROL_STOPPED", "HELPER_UNAVAILABLE", "VERSION_MISMATCH",
      "TIMEOUT", "INVALID_APP", "LAUNCH_FAILED", "INTERNAL",
    ];
    Assert.Equal(expected, Codes());
  }

  [Fact]
  public void EverySurfaceCode_RetrievesAKnownHint()
  {
    foreach (string code in Codes())
    {
      Assert.Contains(ComputerErrorCodes.RetryHint(code), KnownHints);
    }
  }

  [Fact]
  public void UnknownCode_RetrievesRetry() => Assert.Equal("retry", ComputerErrorCodes.RetryHint("MYSTERY"));

  [Theory]
  [InlineData("ELEMENT_UNAVAILABLE", "reobserve")]
  [InlineData("STALE_STATE", "reobserve")]
  [InlineData("CONTROLLER_BUSY", "never")]
  [InlineData("CONTROL_STOPPED", "never")]
  [InlineData("NOT_SETTABLE", "never")]
  [InlineData("NOT_SELECTABLE", "never")]
  [InlineData("ACTION_UNAVAILABLE", "never")]
  [InlineData("VERSION_MISMATCH", "never")]
  [InlineData("APP_NOT_FOUND", "retry")]
  [InlineData("TIMEOUT", "retry")]
  public void RetryHint_MapsTheBriefPartition(string code, string expected) => Assert.Equal(expected, ComputerErrorCodes.RetryHint(code));

  // ---- Commands carry their arguments verbatim ----

  [Fact]
  public void ClickCommand_CarriesEveryArgument()
  {
    ComputerAppRef app = ComputerAppRef.ByName("Notepad");
    ComputerCommand.Click command = new(ComputerTarget.Element(3), app, "right", 2, "ctrl+shift", "a11y", "full");
    Assert.Equal("right", command.MouseButton);
    Assert.Equal(2, command.ClickCount);
    Assert.Equal("ctrl+shift", command.Modifiers);
    Assert.Equal("a11y", command.Strategy);
    Assert.Equal("full", command.ReturnState);
  }

  [Fact]
  public void ListWindowsCommand_CarriesAppRef()
  {
    ComputerCommand.ListWindows command = new(ComputerAppRef.ByPid(9));
    Assert.Equal(9, command.App.Pid);
  }

  [Fact]
  public void ObserveCommand_CarriesScreenshotAndDiffingFlags()
  {
    ComputerCommand.Observe command = new(ComputerAppRef.ByName("Notepad"), true, false);
    Assert.True(command.IncludeScreenshot);
    Assert.False(command.DisableDiffing);
  }

  [Fact]
  public void KeyCommand_RepeatAndHold_AreNullable()
  {
    ComputerCommand.Key command = new("Return", null, null, null, "auto", "none");
    Assert.Null(command.Repeat);
    Assert.Null(command.HoldSeconds);
    ComputerCommand.Key held = new("a", 3, 1.5, null, "auto", "none");
    Assert.Equal(3, held.Repeat);
    Assert.Equal(1.5, held.HoldSeconds);
  }

  [Fact]
  public void StopCommand_ReasonIsNullable()
  {
    Assert.Null(new ComputerCommand.Stop(null).Reason);
    Assert.Equal("done", new ComputerCommand.Stop("done").Reason);
  }

  [Fact]
  public void SelectTextCommand_CarriesSelectionType()
  {
    ComputerCommand.SelectText command = new(
        ComputerTarget.Element(1), "find me", "pre", "post", "cursor_after", null, "compact");
    Assert.Equal("cursor_after", command.SelectionType);
    Assert.Equal("find me", command.Text);
  }

  [Fact]
  public void PasteCommand_CarriesFormat()
  {
    ComputerCommand.Paste command = new("<b>x</b>", null, null, "html", "none");
    Assert.Equal("html", command.Format);
  }
}
