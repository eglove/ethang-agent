using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Task 18 integration: the REAL broker process (BrokerSupervisor spawning the
///     real Host exe) drives the TEST PROCESS'S OWN Win32 window through real UIA and
///     real SendInput. Every test touches only the window this test process created
///     (Win32TestWindow) — never the user's desktop. The watched-failing run of the
///     effect-asserting tests is the batch's RED evidence for the R1 element-ops work.</summary>
[Trait("Category", "Integration")]
[Trait("Requires", "Desktop")]
public sealed class OwnedWindowIntegrationTests(IntegrationWindowFixture fixture) : IClassFixture<IntegrationWindowFixture>
{
  private readonly IntegrationWindowFixture _fixture = fixture;

  private static BrokerComputerAccess NewAccess() => new(NewSupervisor(), ownsSupervisor: true);

  private static BrokerSupervisor NewSupervisor()
  {
    string hostPath = HostExePath();
    string pipeName = "ethang-it-" + Guid.NewGuid().ToString("N");
    return new BrokerSupervisor(hostPath, pipeName, "integration-workspace");
  }


  /// <summary>Observe with brief retries - the desktop environment races fixture windows.</summary>
  private async Task<ComputerOutcome.Observation> ObserveWithRetryAsync(
      BrokerComputerAccess access, bool withScreenshot = false)
  {
    ComputerOutcome last = new ComputerOutcome.Failure(ComputerErrorCodes.Internal, "no attempt made");
    for (int attempt = 0; attempt < 10; attempt++)
    {
      last = await access.ExecuteAsync(
          new ComputerCommand.Observe(ComputerAppRef.ByPid(_fixture.Window.Pid), IncludeScreenshot: withScreenshot, DisableDiffing: false),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      if (last is ComputerOutcome.Observation observation && observation.Elements.Count > 0)
      {
        return observation;
      }

      await Task.Delay(200, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Assert.Fail("observe never returned a usable tree: " + Describe(last));
    return Assert.IsType<ComputerOutcome.Observation>(null); // unreachable
  }

  private static string Describe(ComputerOutcome outcome) => outcome switch
  {
    ComputerOutcome.Observation o => "Observation(elements=" + o.Elements.Count + ")",
    ComputerOutcome.Receipt r => "Receipt(dispatch=" + r.DispatchStatus + ")",
    ComputerOutcome.Failure f => "Failure " + f.Code + ": " + f.Message,
    _ => "unknown outcome",
  };

  private static string HostExePath() =>
      Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src",
          "eThangAgent.ComputerUse.Host", "bin", "Debug", "net10.0-windows",
          "eThangAgent.ComputerUse.Host.exe");


  /// <summary>Waits until the fixture's process has a live window (the pump recreates destroyed
  ///     ones) and returns when stable.</summary>
  private async Task WaitForLiveWindowAsync()
  {
    for (int i = 0; i < 40 && !_fixture.Window.IsWindowAliveNow(); i++)
    {
      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
    Assert.True(_fixture.Window.IsWindowAliveNow(),
        $"no live test window; recreates={_fixture.Window.RecreateCount} err={_fixture.Window.LastRecreateError} " +
        $"creationAlive={_fixture.Window.CreationTimeIsWindow} creatorTid={_fixture.Window.CreatorThreadId} " +
        $"fg={_fixture.Window.CreationForeground} fgPid={_fixture.Window.CreationForegroundPid} " +
        $"fgProcess={_fixture.Window.CreationForegroundProcess} pumpAliveChecks={_fixture.Window.AliveChecks} stopLoop={_fixture.Window.StopLoopRequested}");
  }

  // 1. list_apps finds the test window pid with a name. The desktop environment may close
  // and the pump recreates the fixture window; retry briefly so the race never fails the test.
  [Fact]
  public async Task ListApps_FindsTheTestWindowPid()
  {
    await using BrokerComputerAccess access = NewAccess();
    bool found = false;
    string json = string.Empty;
    for (int attempt = 0; attempt < 10 && !found; attempt++)
    {
      ComputerOutcome outcome = await access.ExecuteAsync(
          new ComputerCommand.ListApps(), TestContext.Current.CancellationToken).ConfigureAwait(true);
      ComputerOutcome.Observation observation = Assert.IsType<ComputerOutcome.Observation>(outcome);
      json = observation.TreeText;
      found = json.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
      if (!found && attempt < 9)
      {
        await Task.Delay(200, TestContext.Current.CancellationToken).ConfigureAwait(true);
      }
    }

    Assert.True(found, "test window never appeared in list_apps: " + json);
  }
  // 2. list_windows returns the test window's id and bounds (rows travel as the tree text).
  // Retried briefly: the desktop environment may close/recreate the fixture window mid-run.
  [Fact]
  public async Task ListWindows_ReturnsWindowIdAndBounds()
  {
    await using BrokerComputerAccess access = NewAccess();
    string json = string.Empty;
    bool found = false;
    for (int attempt = 0; attempt < 10 && !found; attempt++)
    {
      ComputerOutcome outcome = await access.ExecuteAsync(
          new ComputerCommand.ListWindows(ComputerAppRef.ByPid(_fixture.Window.Pid)),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      ComputerOutcome.Observation observation = Assert.IsType<ComputerOutcome.Observation>(outcome);
      json = observation.TreeText;
      found = json.Contains("\"window_id\":", StringComparison.Ordinal);
      if (!found && attempt < 9)
      {
        await Task.Delay(200, TestContext.Current.CancellationToken).ConfigureAwait(true);
      }
    }

    Assert.True(found, "list_windows never returned rows: " + json);
  }

  // 3. observe: tree text carries the button and edit with valid indices; the ledger registers the state.
  [Fact]
  public async Task Observe_ReturnsTreeWithButtonAndEdit_AndRegistersState()
  {
    await WaitForLiveWindowAsync().ConfigureAwait(true);
    await using BrokerComputerAccess access = NewAccess();
    ComputerOutcome.Observation observation = await _fixture.ObserveAsync(access, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Contains(Win32TestWindow.ButtonCaption, observation.TreeText, StringComparison.OrdinalIgnoreCase);
    Assert.Contains(_fixture.Window.LastEditText, observation.TreeText, StringComparison.Ordinal);
    Assert.NotEmpty(observation.Elements);
    Assert.All(observation.Elements, e => Assert.True(e.Index >= 0));
  }

  // 4. set_value on the edit via element index; observe read-back shows the value (R1).
  [Fact]
  public async Task SetValue_OnEdit_ReadsBack()
  {
    await WaitForLiveWindowAsync().ConfigureAwait(true);
    await using BrokerComputerAccess access = NewAccess();
    ComputerOutcome.Observation first = await _fixture.ObserveAsync(access, TestContext.Current.CancellationToken).ConfigureAwait(true);
    int editIndex = IntegrationWindowFixture.IndexOf(first, e => e.Editable);
    Assert.True(editIndex >= 0, "an editable element (the Edit) must appear in the tree");
    ComputerOutcome set = await access.ExecuteAsync(new ComputerCommand.SetValue(
        ComputerTarget.Element(editIndex), "set-over-uia", ComputerAppRef.ByPid(_fixture.Window.Pid)),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = Assert.IsType<ComputerOutcome.Receipt>(set);
    Win32TestWindow.Pump(400);
    Assert.Equal("set-over-uia", _fixture.Window.LastEditText);
  }

  // 5. click the button by element index; asserted via the WndProc counter.
  [Fact]
  public async Task ClickButton_ByElementIndex_IncrementsWndProcCounter()
  {
    await WaitForLiveWindowAsync().ConfigureAwait(true);
    await using BrokerComputerAccess access = NewAccess();
    ComputerOutcome.Observation first = await _fixture.ObserveAsync(access, TestContext.Current.CancellationToken).ConfigureAwait(true);
    int buttonIndex = IntegrationWindowFixture.IndexOf(first, e => e.Pressable);
    Assert.True(buttonIndex >= 0, "a pressable element (the Button) must appear in the tree");
    int before = _fixture.Window.ClickCount;
    ComputerOutcome click = await access.ExecuteAsync(new ComputerCommand.Click(
        ComputerTarget.Element(buttonIndex), ComputerAppRef.ByPid(_fixture.Window.Pid)),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = Assert.IsType<ComputerOutcome.Receipt>(click);
    Win32TestWindow.Pump(500);
    Assert.Equal(before + 1, _fixture.Window.ClickCount);
  }

  // 6. click at a coordinate resolved from a delivered frame; asserted via the WndProc counter.
  // Controller ruling: before ANY SendInput coordinate click, take the fixture window's foreground
  // and verify it (bounded ~2s). If foreground cannot be taken, assert the honest
  // foreground_required refusal instead of clicking - never a blind click.
  [Fact]
  public async Task Click_AtCoordinateFromFrame_IncrementsWndProcCounter()
  {
    await using BrokerComputerAccess access = NewAccess();
    ComputerOutcome.Observation observation = await ObserveWithRetryAsync(access, withScreenshot: true).ConfigureAwait(true);
    Assert.NotNull(observation.Frame);
    Assert.Null(observation.WithheldReason);
    ComputerFrameRef frame = Assert.IsType<ComputerFrameRef>(observation.Frame);
    (int x, int y) = _fixture.ButtonCenterIn(frame);

    bool foregroundTaken = false;
    for (int attempt = 0; attempt < 20 && !foregroundTaken; attempt++)
    {
      foregroundTaken = _fixture.TryTakeForeground();
      if (!foregroundTaken)
      {
        await Task.Delay(100, TestContext.Current.CancellationToken).ConfigureAwait(true);
      }
    }

    if (!foregroundTaken)
    {
      // The safe path: with no verified foreground, only the refusal semantics are exercised.
      ComputerOutcome refused = await access.ExecuteAsync(new ComputerCommand.Click(
          ComputerTarget.Coordinate(x, y), ComputerAppRef.ByPid(_fixture.Window.Pid), Strategy: "event"),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      ComputerOutcome.Failure refusal = Assert.IsType<ComputerOutcome.Failure>(refused);
      Assert.Equal(ComputerErrorCodes.ForegroundRequired, refusal.Code);
      return;
    }

    int before = _fixture.Window.ClickCount;
    ComputerOutcome click = await access.ExecuteAsync(new ComputerCommand.Click(
        ComputerTarget.Coordinate(x, y), ComputerAppRef.ByPid(_fixture.Window.Pid)),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = Assert.IsType<ComputerOutcome.Receipt>(click);
    Win32TestWindow.Pump(500);
    Assert.Equal(before + 1, _fixture.Window.ClickCount);
  }
  // 7. paste into the edit via element target; read back the pasted text.
  [Fact]
  public async Task Paste_IntoEdit_ReadsBackPastedText()
  {
    await WaitForLiveWindowAsync().ConfigureAwait(true);
    await using BrokerComputerAccess access = NewAccess();
    ComputerOutcome.Observation first = await _fixture.ObserveAsync(access, TestContext.Current.CancellationToken).ConfigureAwait(true);
    int editIndex = IntegrationWindowFixture.IndexOf(first, e => e.Editable);
    Assert.True(editIndex >= 0, "an editable element (the Edit) must appear in the tree");
    ComputerOutcome paste = await access.ExecuteAsync(new ComputerCommand.Paste(
        "pasted-text", ComputerTarget.Element(editIndex), ComputerAppRef.ByPid(_fixture.Window.Pid)),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = Assert.IsType<ComputerOutcome.Receipt>(paste);
    Win32TestWindow.Pump(600);
    Assert.EndsWith("pasted-text", _fixture.Window.LastEditText, StringComparison.Ordinal);
  }

  // 8. key action sends a chord; observed via the window's WM_CHAR tracking.
  [Fact]
  public async Task Key_SendsChord_WindowSeesIt()
  {
    await WaitForLiveWindowAsync().ConfigureAwait(true);
    await using BrokerComputerAccess access = NewAccess();
    _ = await _fixture.ObserveAsync(access, TestContext.Current.CancellationToken).ConfigureAwait(true);
    ComputerOutcome key = await access.ExecuteAsync(
        new ComputerCommand.Key("x", Repeat: null, HoldSeconds: null, ComputerAppRef.ByPid(_fixture.Window.Pid)),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = Assert.IsType<ComputerOutcome.Receipt>(key);
    Win32TestWindow.Pump(500);
    Assert.Contains("x", _fixture.Window.RecordedChars, StringComparison.Ordinal);
  }

  // 9. a second concurrent controller gets CONTROLLER_BUSY; after stop releases, actions work again.
  [Fact]
  public async Task SecondController_GetsControllerBusy_OwnerInMessage()
  {
    await using BrokerComputerAccess holder = NewAccess();
    _ = await _fixture.ObserveAsync(holder, TestContext.Current.CancellationToken).ConfigureAwait(true);
    await using BrokerComputerAccess second = NewAccess();
    ComputerOutcome outcome = await second.ExecuteAsync(
        new ComputerCommand.ListApps(), TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = Assert.IsType<ComputerOutcome.Observation>(outcome);
    ComputerOutcome busy = await second.ExecuteAsync(
        new ComputerCommand.Key("y", Repeat: null, HoldSeconds: null, ComputerAppRef.ByPid(_fixture.Window.Pid)),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    ComputerOutcome.Failure failure = Assert.IsType<ComputerOutcome.Failure>(busy);
    Assert.Equal(ComputerErrorCodes.ControllerBusy, failure.Code);
    Assert.Contains("owner=", failure.Message, StringComparison.Ordinal);
  }

  // 10. stop releases the lease; subsequent actions work.
  [Fact]
  public async Task Stop_ReleasesLease_SubsequentActionsWork()
  {
    await WaitForLiveWindowAsync().ConfigureAwait(true);
    await using BrokerComputerAccess access = NewAccess();
    _ = await _fixture.ObserveAsync(access, TestContext.Current.CancellationToken).ConfigureAwait(true);
    ComputerOutcome stop = await access.ExecuteAsync(
        new ComputerCommand.Stop(Reason: "integration test done"), TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = Assert.IsType<ComputerOutcome.Receipt>(stop);
    ComputerOutcome after = await access.ExecuteAsync(
        new ComputerCommand.Key("z", Repeat: null, HoldSeconds: null, ComputerAppRef.ByPid(_fixture.Window.Pid)),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = Assert.IsType<ComputerOutcome.Receipt>(after);
  }

  // 11. killing the broker process: next call fails HELPER_UNAVAILABLE, then one lazy restart succeeds.
  [Fact]
  public async Task BrokerKill_NextCallFailsHelperUnavailable_ThenLazyRestartSucceeds()
  {
    BrokerSupervisor supervisor = NewSupervisor();
    BrokerComputerAccess access = new(supervisor, ownsSupervisor: true);
    try
    {
      _ = await _fixture.ObserveAsync(access, TestContext.Current.CancellationToken).ConfigureAwait(true);
      await supervisor.KillBrokerProcessForTests().ConfigureAwait(true);
      ComputerOutcome outcome = await access.ExecuteAsync(
          new ComputerCommand.ListApps(), TestContext.Current.CancellationToken).ConfigureAwait(true);
      ComputerOutcome.Failure failure = Assert.IsType<ComputerOutcome.Failure>(outcome);
      Assert.Equal(ComputerErrorCodes.HelperUnavailable, failure.Code);
      ComputerOutcome recovered = await access.ExecuteAsync(
          new ComputerCommand.ListApps(), TestContext.Current.CancellationToken).ConfigureAwait(true);
      _ = Assert.IsType<ComputerOutcome.Observation>(recovered);
    }
    finally
    {
      await access.DisposeAsync().ConfigureAwait(true);
    }
  }

  // 12. cancellation honesty: a cancelled observe reports honestly, never silently ok.
  [Fact]
  public async Task CancelledObserve_ReportsHonestFailure()
  {
    await WaitForLiveWindowAsync().ConfigureAwait(true);
    await using BrokerComputerAccess access = NewAccess();
    using CancellationTokenSource cts = new();
    await cts.CancelAsync().ConfigureAwait(true);
    ComputerOutcome outcome = await access.ExecuteAsync(
        new ComputerCommand.Observe(ComputerAppRef.ByPid(_fixture.Window.Pid),
            IncludeScreenshot: false, DisableDiffing: false),
        cts.Token).ConfigureAwait(true);
    if (outcome is ComputerOutcome.Observation)
    {
      Assert.Fail("a pre-cancelled observe must not report a clean observation");
    }
    else
    {
      ComputerOutcome.Failure failure = Assert.IsType<ComputerOutcome.Failure>(outcome);
      Assert.False(string.IsNullOrWhiteSpace(failure.Message));
    }
  }

  // 13. screenshots: include_screenshot returns a non-blank image part.
  [Fact]
  public async Task Observe_WithScreenshot_ReturnsNonBlankImage()
  {
    await WaitForLiveWindowAsync().ConfigureAwait(true);
    await using BrokerComputerAccess access = NewAccess();
    ComputerOutcome outcome = await access.ExecuteAsync(
        new ComputerCommand.Observe(ComputerAppRef.ByPid(_fixture.Window.Pid),
            IncludeScreenshot: true, DisableDiffing: true),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    ComputerOutcome.Observation observation = Assert.IsType<ComputerOutcome.Observation>(outcome);
    ToolResultImage? shot = observation.Screenshot;
    Assert.NotNull(shot);
    Assert.True(shot.Base64Data.Length > 0);
    byte[] jpeg = Convert.FromBase64String(shot.Base64Data);
    Assert.True(jpeg.Length > 100, "a real raster is never a few bytes");
  }
}
