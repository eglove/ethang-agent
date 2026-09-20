namespace eThangAgent.ToolDomain.Tests;

/// <summary>Contract tests for the 'computer' tool (task-11 brief): dispatch to the
///     IComputerAccess seam, the exact output-annotation lines, vision withhold, the
///     effect-evidence advisory, failure formatting with retry hints, and cancellation
///     propagation.</summary>
public class ComputerToolTests
{
  // ---- scripted seam fake ----

  private sealed class ScriptedComputerAccess : IComputerAccess
  {
    public ComputerOutcome Next { get; set; } = new ComputerOutcome.Receipt(false, "accepted", null, null);
    public List<ComputerCommand> Commands { get; } = [];
    public List<CancellationToken> Tokens { get; } = [];

    public Task<ComputerOutcome> ExecuteAsync(ComputerCommand command, CancellationToken ct = default)
    {
      Commands.Add(command);
      Tokens.Add(ct);
      return Task.FromResult(Next);
    }
  }

  private sealed class FixedVision(bool accepts) : IImageInputCapability
  {
    public bool AcceptsImages => accepts;
  }

  private static ComputerTool Make(ScriptedComputerAccess fake, bool vision = true) =>
      new(fake, new FixedVision(vision));

  private static string Args(string inner) =>
      "{" + "\"timeoutSeconds\":120" + (inner.Length == 0 ? "" : "," + inner) + "}";

  private static async Task<ToolResult> Call(ScriptedComputerAccess fake, string inner, bool vision = true)
  {
    ComputerTool tool = Make(fake, vision);
    return await tool.ExecuteAsync(new RawToolInput("computer", Args(inner)),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  private static ComputerOutcome.Observation Obs(string tree = "# tree", bool withImage = false) => new(
      tree,
      "state-1",
      [new ComputerElement(3, "button", "Button", "OK", null, [1, 2, 3, 4], true, false,
          ["Invoke"], false, false, true, false, null, null, null)],
      new ComputerFrameRef("frame-1", 800, 600),
      null,
      withImage ? new ToolResultImage("image/png", Convert.ToBase64String([1, 2, 3])) : null);

  // ---- definition contract ----

  [Fact]
  public void Definition_NameAndRequired()
  {
    ComputerTool tool = Make(new ScriptedComputerAccess());
    Assert.Equal("computer", tool.Definition.Name);
    Assert.Equal(["timeoutSeconds", "action"], tool.Definition.RequiredParameters);
  }

  [Fact]
  public void Definition_DescriptionDocumentsActionsKeysAndOutputLines()
  {
    string d = Make(new ScriptedComputerAccess()).Definition.Description;
    // every action
    foreach (string a in new[] { "list_apps", "list_windows", "observe", "click", "drag",
             "scroll", "type", "set_value", "select_text", "key", "paste", "perform_action", "stop" })
    {
      Assert.Contains(a, d, StringComparison.Ordinal);
    }

    // key surface
    foreach (string k in new[] { "app_ref", "include_screenshot", "disable_diffing", "target",
             "mouse_button", "click_count", "modifiers", "strategy", "return_state", "from_target",
             "to", "scroll_direction", "scroll_amount", "text", "value", "prefix", "suffix",
             "selection_type", "repeat", "hold_seconds", "format", "action_name", "reason" })
    {
      Assert.Contains(k, d, StringComparison.Ordinal);
    }

    // output contract lines verbatim
    Assert.Contains("[computer] ok", d, StringComparison.Ordinal);
    Assert.Contains("dispatch=", d, StringComparison.Ordinal);
    Assert.Contains("[computer] screenshot withheld: model has no image input", d, StringComparison.Ordinal);
    Assert.Contains("[computer] effect_evidence=unchanged", d, StringComparison.Ordinal);
    Assert.Contains("Error [Code]:", d, StringComparison.Ordinal);
    Assert.Contains("retry=", d, StringComparison.Ordinal);

    // error codes
    Assert.Contains("APP_NOT_FOUND", d, StringComparison.Ordinal);
    Assert.Contains("ELEMENT_UNAVAILABLE", d, StringComparison.Ordinal);

    // ranges
    Assert.Contains("0..100", d, StringComparison.Ordinal);
    Assert.Contains("1..100", d, StringComparison.Ordinal);
    Assert.Contains("0..30", d, StringComparison.Ordinal);
  }

  // ---- parser errors surface as Error [code]: message ----

  [Fact]
  public async Task UnknownAction_SurfacesParserError()
  {
    ToolResult result = await Call(new ScriptedComputerAccess(), "\"action\":\"warp\"");
    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidAction]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingAppRef_SurfacesMissingParameter()
  {
    ToolResult result = await Call(new ScriptedComputerAccess(), "\"action\":\"observe\"");
    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task OutOfRangeScrollAmount_SurfacesInvalidParameterValue()
  {
    ToolResult result = await Call(new ScriptedComputerAccess(),
        "\"action\":\"scroll\",\"target\":{\"type\":\"element\",\"index\":1},\"scroll_direction\":\"down\",\"scroll_amount\":101");
    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ParserError_NeverTouchesTheSeam()
  {
    ScriptedComputerAccess fake = new();
    _ = await Call(fake, "\"action\":\"warp\"");
    Assert.Empty(fake.Commands);
  }

  // ---- observe: tree as content, images attach, withhold notice ----

  [Fact]
  public async Task Observe_RendersTreeTextAsContent()
  {
    ScriptedComputerAccess fake = new() { Next = Obs() };
    ToolResult result = await Call(fake, "\"action\":\"observe\",\"app_ref\":{\"pid\":42}");
    Assert.False(result.IsError);
    Assert.Equal("# tree", result.Content);
    Assert.Null(result.Images);
    ComputerCommand.Observe command = Assert.IsType<ComputerCommand.Observe>(Assert.Single(fake.Commands));
    Assert.False(command.IncludeScreenshot);
  }

  [Fact]
  public async Task Observe_ScreenshotAttachesWhenModelAcceptsImages()
  {
    ScriptedComputerAccess fake = new() { Next = Obs(withImage: true) };
    ToolResult result = await Call(fake,
        "\"action\":\"observe\",\"app_ref\":{\"pid\":42},\"include_screenshot\":true", vision: true);
    Assert.False(result.IsError);
    Assert.Equal("# tree", result.Content);
    ToolResultImage image = Assert.Single(result.Images!);
    Assert.Equal("image/png", image.MediaType);
    ComputerCommand.Observe command = Assert.IsType<ComputerCommand.Observe>(Assert.Single(fake.Commands));
    Assert.True(command.IncludeScreenshot);
  }

  [Fact]
  public async Task Observe_ScreenshotWithheldWhenModelHasNoImageInput()
  {
    ScriptedComputerAccess fake = new() { Next = Obs(withImage: true) };
    ToolResult result = await Call(fake,
        "\"action\":\"observe\",\"app_ref\":{\"pid\":42},\"include_screenshot\":true", vision: false);
    Assert.False(result.IsError);
    Assert.Contains("# tree", result.Content, StringComparison.Ordinal);
    Assert.Contains("[computer] screenshot withheld: model has no image input", result.Content, StringComparison.Ordinal);
    Assert.Null(result.Images);
    ComputerCommand.Observe command = Assert.IsType<ComputerCommand.Observe>(Assert.Single(fake.Commands));
    Assert.True(command.IncludeScreenshot);
  }

  [Fact]
  public async Task Observe_RequestedScreenshotButSeamReturnsNone_AttachesNothing()
  {
    ScriptedComputerAccess fake = new() { Next = Obs() };
    ToolResult result = await Call(fake,
        "\"action\":\"observe\",\"app_ref\":{\"pid\":42},\"include_screenshot\":true", vision: true);
    Assert.False(result.IsError);
    Assert.Null(result.Images);
    Assert.DoesNotContain("withheld", result.Content, StringComparison.Ordinal);
  }

  // ---- receipt: ok line, dispatch status, return_state tree, advisory ----

  [Fact]
  public async Task Click_RendersExactOkLine()
  {
    ScriptedComputerAccess fake = new() { Next = new ComputerOutcome.Receipt(true, "accepted", null, null) };
    ToolResult result = await Call(fake, "\"action\":\"click\",\"target\":{\"type\":\"element\",\"index\":3}");
    Assert.False(result.IsError);
    Assert.Equal("[computer] ok click action_sent=true dispatch=accepted", result.Content);
  }

  [Fact]
  public async Task Stop_RendersOkLineWithPossiblySent()
  {
    ScriptedComputerAccess fake = new() { Next = new ComputerOutcome.Receipt(false, "possibly_sent", null, null) };
    ToolResult result = await Call(fake, "\"action\":\"stop\"");
    Assert.False(result.IsError);
    Assert.Equal("[computer] ok stop action_sent=false dispatch=possibly_sent", result.Content);
  }

  [Fact]
  public async Task Receipt_TreeTextAppendsPostActionTree()
  {
    ScriptedComputerAccess fake = new() { Next = new ComputerOutcome.Receipt(true, "accepted", null, "# post tree") };
    ToolResult result = await Call(fake,
        "\"action\":\"click\",\"target\":{\"type\":\"element\",\"index\":3},\"return_state\":\"compact\"");
    Assert.False(result.IsError);
    Assert.Equal("[computer] ok click action_sent=true dispatch=accepted\n# post tree", result.Content);
  }

  [Fact]
  public async Task Receipt_UnchangedEffectEvidence_AppendsAdvisoryOnPointerAction()
  {
    ScriptedComputerAccess fake = new() { Next = new ComputerOutcome.Receipt(true, "accepted", "unchanged", null) };
    ToolResult result = await Call(fake, "\"action\":\"click\",\"target\":{\"type\":\"element\",\"index\":3}");
    Assert.False(result.IsError);
    Assert.Contains("[computer] effect_evidence=unchanged - the app state is byte-identical; do not repeat the same coordinate; prefer an element index or keyboard",
        result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Receipt_ChangedEffectEvidence_NoAdvisory()
  {
    ScriptedComputerAccess fake = new() { Next = new ComputerOutcome.Receipt(true, "accepted", "clicked", null) };
    ToolResult result = await Call(fake, "\"action\":\"click\",\"target\":{\"type\":\"element\",\"index\":3}");
    Assert.False(result.IsError);
    Assert.DoesNotContain("effect_evidence=unchanged", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Receipt_UnchangedEvidence_OnKeyboardAction_NoAdvisory()
  {
    ScriptedComputerAccess fake = new() { Next = new ComputerOutcome.Receipt(true, "accepted", "unchanged", null) };
    ToolResult result = await Call(fake, "\"action\":\"key\",\"text\":\"Return\"");
    Assert.False(result.IsError);
    Assert.DoesNotContain("effect_evidence=unchanged", result.Content, StringComparison.Ordinal);
  }

  // ---- failure formatting with retry hints ----

  [Theory]
  [InlineData("APP_NOT_FOUND", "retry")]
  [InlineData("ELEMENT_UNAVAILABLE", "reobserve")]
  [InlineData("STALE_STATE", "reobserve")]
  [InlineData("CONTROLLER_BUSY", "never")]
  [InlineData("NOT_SETTABLE", "never")]
  [InlineData("INTERNAL", "retry")]
  public async Task Failure_RendersErrorBlockWithRetryHint(string code, string hint)
  {
    ScriptedComputerAccess fake = new() { Next = new ComputerOutcome.Failure(code, "boom") };
    ToolResult result = await Call(fake, "\"action\":\"click\",\"target\":{\"type\":\"element\",\"index\":3}");
    Assert.True(result.IsError);
    Assert.Contains("Error [" + code + "]: boom", result.Content, StringComparison.Ordinal);
    Assert.Contains("retry=" + hint, result.Content, StringComparison.Ordinal);
  }

  // ---- command building and cancellation ----

  [Fact]
  public async Task Commands_BuildSeamRecordsFromValidInput()
  {
    ScriptedComputerAccess fake = new() { Next = new ComputerOutcome.Receipt(true, "accepted", null, null) };
    _ = await Call(fake, "\"action\":\"type\",\"text\":\"hi\",\"return_state\":\"full\"");
    ComputerCommand.TypeText command = Assert.IsType<ComputerCommand.TypeText>(Assert.Single(fake.Commands));
    Assert.Equal("hi", command.Text);
    Assert.Equal("full", command.ReturnState);
  }

  [Fact]
  public async Task CallerCancellation_PropagatesIntoTheSeam()
  {
    using CancellationTokenSource cts = new();
    await cts.CancelAsync().ConfigureAwait(true);
    ScriptedComputerAccess fake = new();
    ComputerTool tool = Make(fake);
    // the caller's token rides into the seam's ExecuteAsync
    _ = await tool.ExecuteAsync(new RawToolInput("computer", Args("\"action\":\"list_apps\"")), cts.Token).ConfigureAwait(true);
    Assert.True(Assert.Single(fake.Tokens).IsCancellationRequested);
  }
}
