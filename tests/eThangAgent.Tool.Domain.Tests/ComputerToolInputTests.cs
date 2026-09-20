using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Parser contract tests for the computer tool (task-10 brief): strict
///     JSON argument parsing for all thirteen actions — required/optional keys,
///     exact action names, app_ref and target sub-objects, and range validation
///     with no clamping.</summary>
public class ComputerToolInputTests
{
  private static string WithTimeout(string inner) =>
      "{" + "\"timeoutSeconds\":120" + (inner.Length == 0 ? "" : "," + inner) + "}";

  private const string AppRef = "\"app_ref\":{\"pid\":42}";

  private const string ElementTarget = "\"target\":{\"type\":\"element\",\"index\":3}";

  private const string CoordTarget = "\"target\":{\"type\":\"coordinate\",\"x\":10,\"y\":20}";

  private static Result<ComputerToolInput> Parse(string json) => ComputerToolInput.Create(json);

  private static ComputerToolInput ParseOk(string json)
  {
    Result<ComputerToolInput> parsed = Parse(json);
    Assert.True(parsed.IsSuccess, "parse failed: " + (parsed.Error?.Code ?? "?") + ": " + (parsed.Error?.Message ?? ""));
    return parsed.Value;
  }

  private static string ErrorCode(string json)
  {
    Result<ComputerToolInput> parsed = Parse(json);
    Assert.False(parsed.IsSuccess, "expected failure but parse succeeded");
    return parsed.Error.Code;
  }

  // ---- happy paths ----

  [Fact]
  public void ListApps_Parses()
  {
    ComputerToolInput input = ParseOk(WithTimeout("\"action\":\"list_apps\""));
    Assert.Equal(ComputerAction.ListApps, input.Action);
    Assert.Null(input.AppRef);
  }

  [Fact]
  public void ListWindows_ParsesAppRef()
  {
    ComputerToolInput input = ParseOk(WithTimeout("\"action\":\"list_windows\"," + AppRef));
    Assert.Equal(ComputerAction.ListWindows, input.Action);
    Assert.Equal(42, input.AppRef!.Pid);
  }

  [Fact]
  public void Observe_ParsesFlags()
  {
    ComputerToolInput input = ParseOk(WithTimeout(
        "\"action\":\"observe\"," + AppRef + ",\"include_screenshot\":true,\"disable_diffing\":true"));
    Assert.True(input.IncludeScreenshot!.Value);
    Assert.True(input.DisableDiffing!.Value);
  }

  [Fact]
  public void Click_ParsesWithAllOptions()
  {
    ComputerToolInput input = ParseOk(WithTimeout(
        "\"action\":\"click\"," + ElementTarget + ",\"app_ref\":{\"name\":\"Notepad\"}," +
        "\"mouse_button\":\"right\",\"click_count\":2,\"modifiers\":\"ctrl+shift\"," +
        "\"strategy\":\"a11y\",\"return_state\":\"full\""));
    ComputerCommand.Click command = Assert.IsType<ComputerCommand.Click>(input.ToCommand());
    Assert.Equal(ComputerTarget.Element(3), command.Target);
    Assert.Equal("right", command.MouseButton);
    Assert.Equal(2, command.ClickCount);
    Assert.Equal("ctrl+shift", command.Modifiers);
    Assert.Equal("a11y", command.Strategy);
    Assert.Equal("full", command.ReturnState);
  }

  [Fact]
  public void Click_ParsesCoordinateTarget()
  {
    ComputerToolInput input = ParseOk(WithTimeout("\"action\":\"click\"," + CoordTarget));
    ComputerCommand.Click command = Assert.IsType<ComputerCommand.Click>(input.ToCommand());
    Assert.Equal(ComputerTarget.Coordinate(10, 20), command.Target);
  }

  [Fact]
  public void Drag_ParsesTargets()
  {
    ComputerToolInput input = ParseOk(WithTimeout(
        "\"action\":\"drag\",\"from_target\":{\"type\":\"element\",\"index\":3},\"to\":{\"type\":\"coordinate\",\"x\":10,\"y\":20}"));
    ComputerCommand.Drag command = Assert.IsType<ComputerCommand.Drag>(input.ToCommand());
    Assert.Equal(ComputerTarget.Element(3), command.From);
    Assert.Equal(ComputerTarget.Coordinate(10, 20), command.To);
  }

  [Fact]
  public void Scroll_ParsesDirectionAndAmount()
  {
    ComputerToolInput input = ParseOk(WithTimeout(
        "\"action\":\"scroll\"," + ElementTarget + ",\"scroll_direction\":\"down\",\"scroll_amount\":3"));
    ComputerCommand.Scroll command = Assert.IsType<ComputerCommand.Scroll>(input.ToCommand());
    Assert.Equal("down", command.ScrollDirection);
    Assert.Equal(3, command.ScrollAmount);
  }

  [Fact]
  public void Type_ParsesText()
  {
    ComputerToolInput input = ParseOk(WithTimeout("\"action\":\"type\",\"text\":\"hello world\""));
    ComputerCommand.TypeText command = Assert.IsType<ComputerCommand.TypeText>(input.ToCommand());
    Assert.Equal("hello world", command.Text);
    Assert.Null(command.Target);
  }

  [Fact]
  public void SetValue_ParsesTargetAndValue()
  {
    ComputerToolInput input = ParseOk(WithTimeout(
        "\"action\":\"set_value\"," + ElementTarget + ",\"value\":\"new text\""));
    ComputerCommand.SetValue command = Assert.IsType<ComputerCommand.SetValue>(input.ToCommand());
    Assert.Equal("new text", command.Value);
  }

  [Fact]
  public void SelectText_ParsesSelection()
  {
    ComputerToolInput input = ParseOk(WithTimeout(
        "\"action\":\"select_text\"," + ElementTarget + ",\"text\":\"find me\",\"selection_type\":\"cursor_after\""));
    ComputerCommand.SelectText command = Assert.IsType<ComputerCommand.SelectText>(input.ToCommand());
    Assert.Equal("find me", command.Text);
    Assert.Equal("cursor_after", command.SelectionType);
    Assert.Null(command.Prefix);
    Assert.Null(command.Suffix);
  }

  [Fact]
  public void Key_ParsesRepeatAndHold()
  {
    ComputerToolInput input = ParseOk(WithTimeout(
        "\"action\":\"key\",\"text\":\"Return\",\"repeat\":3,\"hold_seconds\":2"));
    ComputerCommand.Key command = Assert.IsType<ComputerCommand.Key>(input.ToCommand());
    Assert.Equal("Return", command.Text);
    Assert.Equal(3, command.Repeat);
    Assert.Equal(2, command.HoldSeconds);
  }

  [Fact]
  public void Paste_ParsesFormat()
  {
    ComputerToolInput input = ParseOk(WithTimeout(
        "\"action\":\"paste\",\"text\":\"<b>x</b>\",\"format\":\"html\""));
    ComputerCommand.Paste command = Assert.IsType<ComputerCommand.Paste>(input.ToCommand());
    Assert.Equal("html", command.Format);
  }

  [Fact]
  public void PerformAction_ParsesActionName()
  {
    ComputerToolInput input = ParseOk(WithTimeout(
        "\"action\":\"perform_action\"," + ElementTarget + ",\"action_name\":\"ExpandCollapse\""));
    ComputerCommand.PerformAction command = Assert.IsType<ComputerCommand.PerformAction>(input.ToCommand());
    Assert.Equal("ExpandCollapse", command.Action);
  }

  [Fact]
  public void Stop_ParsesReason()
  {
    ComputerToolInput input = ParseOk(WithTimeout("\"action\":\"stop\",\"reason\":\"done\""));
    ComputerCommand.Stop command = Assert.IsType<ComputerCommand.Stop>(input.ToCommand());
    Assert.Equal("done", command.Reason);
  }

  // ---- action name ----

  [Theory]
  [InlineData("\"List\"")]
  [InlineData("\"LIST_APPS\"")]
  [InlineData("\"observe_typo\"")]
  public void UnknownAction_Fails(string action) => Assert.Equal("InvalidAction", ErrorCode(WithTimeout("\"action\":" + action)));

  [Fact]
  public void MissingAction_Fails() => Assert.Equal("InvalidAction", ErrorCode(WithTimeout("")));

  [Fact]
  public void WrongTypeAction_Fails() => Assert.Equal("InvalidParameterType", ErrorCode(WithTimeout("\"action\":42")));

  // ---- unknown keys per action ----

  [Theory]
  [InlineData("list_apps", "stat")]
  [InlineData("observe", "target")]
  [InlineData("click", "text")]
  [InlineData("type", "scroll_amount")]
  [InlineData("stop", "target")]
  public void UnknownKey_Fails(string action, string key)
  {
    string baseArgs = action switch
    {
      "observe" => AppRef,
      "click" => ElementTarget,
      "type" => "\"text\":\"x\"",
      _ => "",
    };
    string json = WithTimeout("\"action\":\"" + action + "\"" +
        (baseArgs.Length == 0 ? "" : "," + baseArgs) + ",\"" + key + "\":1");
    Assert.Equal("UnknownParameter", ErrorCode(json));
  }

  // ---- wrong JSON kinds ----

  [Theory]
  [InlineData("\"app_ref\":\"Notepad\"")]
  [InlineData("\"app_ref\":42")]
  public void AppRef_BareStringShorthand_NotAccepted(string appRef) => Assert.Equal("InvalidParameterType", ErrorCode(WithTimeout("\"action\":\"observe\"," + appRef)));

  [Fact]
  public void Target_WrongKind_Fails() => Assert.Equal("InvalidParameterType", ErrorCode(WithTimeout("\"action\":\"click\",\"target\":\"[3]\"")));

  [Fact]
  public void Text_WrongKind_Fails() => Assert.Equal("InvalidParameterType", ErrorCode(WithTimeout("\"action\":\"type\",\"text\":7")));

  // ---- missing required keys ----

  [Theory]
  [InlineData("list_windows", "app_ref")]
  [InlineData("observe", "app_ref")]
  [InlineData("click", "target")]
  [InlineData("scroll", "scroll_direction")]
  [InlineData("type", "text")]
  [InlineData("set_value", "value")]
  [InlineData("select_text", "text")]
  [InlineData("key", "text")]
  [InlineData("paste", "text")]
  [InlineData("perform_action", "action_name")]
  public void MissingRequiredKey_Fails(string action, string key)
  {
    // every OTHER required key of the action is supplied, so the asserted-absent
    // key is the only one missing and the error names exactly it
    string baseArgs = action switch
    {
      // click carries exactly one required key (target): supply nothing else
      "scroll" => ElementTarget + ",\"scroll_amount\":1",
      "set_value" or "select_text" or "perform_action" => ElementTarget,
      _ => "",
    };
    string json = WithTimeout("\"action\":\"" + action + "\"" + (baseArgs.Length == 0 ? "" : "," + baseArgs));
    string code = ErrorCode(json);
    Assert.Equal("MissingParameter", code);
    Assert.Contains("'" + key + "'", Parse(json).Error!.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void MissingSelectTextText_Fails()
  {
    // select_text carries two required keys: target present, text absent
    string json = WithTimeout("\"action\":\"select_text\"," + ElementTarget);
    string code = ErrorCode(json);
    Assert.Equal("MissingParameter", code);
    Assert.Contains("'text'", Parse(json).Error!.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void MissingSetValueTarget_Fails()
  {
    // set_value carries two required keys: value present, target absent
    string json = WithTimeout("\"action\":\"set_value\",\"value\":\"v\"");
    string code = ErrorCode(json);
    Assert.Equal("MissingParameter", code);
    Assert.Contains("'target'", Parse(json).Error!.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void MissingPerformActionTarget_Fails()
  {
    // perform_action carries two required keys: action_name present, target absent
    string json = WithTimeout("\"action\":\"perform_action\",\"action_name\":\"Invoke\"");
    string code = ErrorCode(json);
    Assert.Equal("MissingParameter", code);
    Assert.Contains("'target'", Parse(json).Error!.Message, StringComparison.Ordinal);
  }

  // ---- app_ref violations ----

  [Fact]
  public void AppRef_EmptyObject_Fails() => Assert.Equal("InvalidParameterValue", ErrorCode(WithTimeout("\"action\":\"observe\",\"app_ref\":{}")));

  [Fact]
  public void AppRef_TwoSelectors_Fails()
  {
    string json = WithTimeout("\"action\":\"observe\",\"app_ref\":{\"name\":\"Notepad\",\"pid\":42}");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void AppRef_NegativeWindowId_Fails()
  {
    string json = WithTimeout("\"action\":\"observe\",\"app_ref\":{\"pid\":42,\"window_id\":-1}");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void AppRef_WindowId_IsOptional()
  {
    ComputerToolInput input = ParseOk(WithTimeout("\"action\":\"observe\",\"app_ref\":{\"pid\":42,\"window_id\":7}"));
    Assert.Equal(7, input.AppRef!.WindowId);
  }

  // ---- target violations ----

  [Fact]
  public void Target_MissingType_Fails()
  {
    string json = WithTimeout("\"action\":\"click\",\"target\":{\"index\":3}");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void Target_UnknownType_Fails()
  {
    string json = WithTimeout("\"action\":\"click\",\"target\":{\"type\":\"pixel\",\"x\":1,\"y\":2}");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void Target_ElementNegativeIndex_Fails()
  {
    string json = WithTimeout("\"action\":\"click\",\"target\":{\"type\":\"element\",\"index\":-1}");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void Target_CoordinateNegative_Fails()
  {
    string json = WithTimeout("\"action\":\"click\",\"target\":{\"type\":\"coordinate\",\"x\":-5,\"y\":2}");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void Target_CoordinateMissingY_Fails()
  {
    string json = WithTimeout("\"action\":\"click\",\"target\":{\"type\":\"coordinate\",\"x\":5}");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  // ---- ranges: no clamping ----

  [Fact]
  public void ScrollAmount_Zero_IsAccepted()
  {
    ComputerToolInput input = ParseOk(WithTimeout(
        "\"action\":\"scroll\"," + ElementTarget + ",\"scroll_direction\":\"up\",\"scroll_amount\":0"));
    Assert.Equal(0, Assert.IsType<ComputerCommand.Scroll>(input.ToCommand()).ScrollAmount);
  }

  [Fact]
  public void ScrollAmount_Above100_Fails()
  {
    string json = WithTimeout("\"action\":\"scroll\"," + ElementTarget + ",\"scroll_direction\":\"up\",\"scroll_amount\":101");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void ScrollDirection_Unknown_Fails()
  {
    string json = WithTimeout("\"action\":\"scroll\"," + ElementTarget + ",\"scroll_direction\":\"sideways\",\"scroll_amount\":1");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void MouseButton_Unknown_Fails()
  {
    string json = WithTimeout("\"action\":\"click\"," + ElementTarget + ",\"mouse_button\":\"middleish\"");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void ClickCount_Zero_Fails()
  {
    string json = WithTimeout("\"action\":\"click\"," + ElementTarget + ",\"click_count\":0");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void Repeat_Above100_Fails()
  {
    string json = WithTimeout("\"action\":\"key\",\"text\":\"a\",\"repeat\":101");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void Repeat_Zero_Fails()
  {
    string json = WithTimeout("\"action\":\"key\",\"text\":\"a\",\"repeat\":0");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void HoldSeconds_Above30_Fails()
  {
    string json = WithTimeout("\"action\":\"key\",\"text\":\"a\",\"hold_seconds\":30.5");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void HoldSeconds_Negative_Fails()
  {
    string json = WithTimeout("\"action\":\"key\",\"text\":\"a\",\"hold_seconds\":-1");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void Modifiers_UnknownToken_Fails()
  {
    string json = WithTimeout("\"action\":\"click\"," + ElementTarget + ",\"modifiers\":\"ctrl+hyper\"");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void Strategy_Unknown_Fails()
  {
    // every strategy-carrying action rejects a value outside auto|a11y|event
    string[] jsons =
    [
      Click("strategy", "psy"),
      Scroll("strategy", "auto-ish"),
      Type("strategy", "A11Y"),
      SetValue("strategy", "x"),
      Key("strategy", "autox"),
    ];
    foreach (string json in jsons)
    {
      Assert.Equal("InvalidParameterValue", ErrorCode(json));
    }
  }

  [Fact]
  public void ReturnState_Unknown_Fails()
  {
    // every return_state-carrying action rejects a value outside compact|full|none
    string[] jsons =
    [
      Click("return_state", "some"),
      Drag("return_state", "some"),
      Scroll("return_state", "some"),
      Type("return_state", "some"),
      SetValue("return_state", "some"),
      SelectText("return_state", "some"),
      Key("return_state", "some"),
      Paste("return_state", "some"),
      PerformAction("return_state", "some"),
    ];
    foreach (string json in jsons)
    {
      Assert.Equal("InvalidParameterValue", ErrorCode(json));
    }
  }

  [Fact]
  public void SelectionType_Unknown_Fails()
  {
    string json = WithTimeout("\"action\":\"select_text\"," + ElementTarget + ",\"text\":\"x\",\"selection_type\":\"middle\"");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void PasteFormat_Unknown_Fails()
  {
    string json = WithTimeout("\"action\":\"paste\",\"text\":\"x\",\"format\":\"rtf\"");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void PerformAction_EmptyActionName_Fails()
  {
    string json = WithTimeout("\"action\":\"perform_action\"," + ElementTarget + ",\"action_name\":\"\"");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }


  [Fact]
  public void Type_EmptyText_Fails()
  {
    string json = WithTimeout("\"action\":\"type\",\"text\":\"\"");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void ReturnState_CompactFullNone_AreAccepted()
  {
    foreach (string s in new[] { "compact", "full", "none" })
    {
      ComputerToolInput input = ParseOk(WithTimeout(
          "\"action\":\"click\"," + ElementTarget + ",\"return_state\":\"" + s + "\""));
      Assert.Equal(s, input.ReturnState);
    }
  }

  // ---- per-action JSON builders for the membership tests ----

  private static string Click(string key, string value) =>
      WithTimeout("\"action\":\"click\"," + ElementTarget + ",\"" + key + "\":\"" + value + "\"");

  private static string Drag(string key, string value) =>
      WithTimeout("\"action\":\"drag\",\"from_target\":{\"type\":\"element\",\"index\":3},\"to\":{\"type\":\"coordinate\",\"x\":1,\"y\":2},\"" + key + "\":\"" + value + "\"");

  private static string Scroll(string key, string value) =>
      WithTimeout("\"action\":\"scroll\"," + ElementTarget + ",\"scroll_direction\":\"down\",\"scroll_amount\":1,\"" + key + "\":\"" + value + "\"");

  private static string Type(string key, string value) =>
      WithTimeout("\"action\":\"type\",\"text\":\"x\",\"" + key + "\":\"" + value + "\"");

  private static string SetValue(string key, string value) =>
      WithTimeout("\"action\":\"set_value\"," + ElementTarget + ",\"value\":\"v\",\"" + key + "\":\"" + value + "\"");

  private static string SelectText(string key, string value) =>
      WithTimeout("\"action\":\"select_text\"," + ElementTarget + ",\"text\":\"x\",\"" + key + "\":\"" + value + "\"");

  private static string Key(string key, string value) =>
      WithTimeout("\"action\":\"key\",\"text\":\"x\",\"" + key + "\":\"" + value + "\"");

  private static string Paste(string key, string value) =>
      WithTimeout("\"action\":\"paste\",\"text\":\"x\",\"" + key + "\":\"" + value + "\"");

  private static string PerformAction(string key, string value) =>
      WithTimeout("\"action\":\"perform_action\"," + ElementTarget + ",\"action_name\":\"Invoke\",\"" + key + "\":\"" + value + "\"");

  // ---- strict sub-object shapes ----



  [Fact]
  public void Drag_RequiresToTarget()
  {
    string json = WithTimeout(
        "\"action\":\"drag\",\"from_target\":{\"type\":\"element\",\"index\":3}");
    Assert.Equal("MissingParameter", ErrorCode(json));
    Assert.Contains("'to'", Parse(json).Error!.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("ctrl+shift+win")]
  [InlineData("ctrl+alt+shift+win")]
  public void Modifier_TokenShapes_Parse(string modifiers)
  {
    ComputerToolInput input = ParseOk(WithTimeout(
        "\"action\":\"click\"," + ElementTarget + ",\"modifiers\":\"" + modifiers + "\""));
    Assert.Equal(modifiers, input.Modifiers);
  }

  // ---- fix round 1 (F1): drag modifiers validated like click's ----

  [Fact]
  public void Drag_UnknownModifierToken_Fails()
  {
    string json = WithTimeout("\"action\":\"drag\",\"from_target\":{\"type\":\"element\",\"index\":3},\"to\":{\"type\":\"coordinate\",\"x\":1,\"y\":2},\"modifiers\":\"ctrl+hyper\"");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  // ---- fix round 1 (F2): interior unknown keys inside app_ref and target objects ----

  [Theory]
  [InlineData("label")]
  [InlineData("extra")]
  [InlineData("note")]
  public void AppRef_InteriorUnknownKey_Fails(string junkKey)
  {
    string appRef = "{\"pid\":42" + junkKeyFragment(junkKey) + "}";
    string json = WithTimeout("\"action\":\"observe\",\"app_ref\":" + appRef);
    Assert.Equal("UnknownParameter", ErrorCode(json));
  }

  [Theory]
  [InlineData("role")]
  [InlineData("z")]
  public void Target_InteriorUnknownKey_Fails(string junkKey)
  {
    string target = junkKey == "role"
      ? "{\"type\":\"element\",\"index\":3" + junkKeyFragment(junkKey) + "}"
      : "{\"type\":\"coordinate\",\"x\":1,\"y\":2" + junkKeyFragment(junkKey) + "}";
    string json = WithTimeout("\"action\":\"click\",\"target\":" + target);
    Assert.Equal("UnknownParameter", ErrorCode(json));
  }

  /// <summary>Builds a `,"key":"v"` fragment without a JSON-looking literal.
  ///     (The JSON002 analyzer fires on InlineData strings that parse as JSON.)</summary>
  private static string junkKeyFragment(string key) =>
      "," + "\"" + key + "\":" + "\"" + "v" + "\"";

  // ---- fix round 1 (F4): empty modifier tokens are rejected ----

  [Theory]
  [InlineData("ctrl++shift")]
  [InlineData("ctrl+")]
  [InlineData("+shift")]
  public void Modifier_EmptyTokens_Fail(string modifiers)
  {
    string json = WithTimeout("\"action\":\"click\"," + ElementTarget + ",\"modifiers\":\"" + modifiers + "\"");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  // ---- fix round 1 (F5): hold_seconds is whole seconds only ----

  [Fact]
  public void HoldSeconds_Fractional_Fails()
  {
    string json = WithTimeout("\"action\":\"key\",\"text\":\"a\",\"hold_seconds\":1.5");
    Assert.Equal("InvalidParameterValue", ErrorCode(json));
  }

  [Fact]
  public void HoldSeconds_WholeSeconds_AreAccepted()
  {
    ComputerToolInput input = ParseOk(WithTimeout("\"action\":\"key\",\"text\":\"a\",\"hold_seconds\":2"));
    Assert.Equal(2, input.HoldSeconds);
  }

  // ---- fix round 1 (F7): matrix holes ----

  [Fact]
  public void MissingScrollAmount_Fails()
  {
    string json = WithTimeout("\"action\":\"scroll\"," + ElementTarget + ",\"scroll_direction\":\"down\"");
    string code = ErrorCode(json);
    Assert.Equal("MissingParameter", code);
    Assert.Contains("'scroll_amount'", Parse(json).Error!.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void MissingSelectTextTarget_Fails()
  {
    string json = WithTimeout("\"action\":\"select_text\",\"text\":\"x\"");
    string code = ErrorCode(json);
    Assert.Equal("MissingParameter", code);
    Assert.Contains("'target'", Parse(json).Error!.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void MissingDragFromTarget_Fails()
  {
    string json = WithTimeout("\"action\":\"drag\",\"to\":{\"type\":\"coordinate\",\"x\":1,\"y\":2}");
    string code = ErrorCode(json);
    Assert.Equal("MissingParameter", code);
    Assert.Contains("'from_target'", Parse(json).Error!.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(1)]
  [InlineData(100)]
  public void Repeat_Boundaries_AreAccepted(int repeat)
  {
    ComputerToolInput input = ParseOk(WithTimeout("\"action\":\"key\",\"text\":\"a\",\"repeat\":" + repeat));
    Assert.Equal(repeat, input.Repeat);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(30)]
  public void HoldSeconds_Boundaries_AreAccepted(int holdSeconds)
  {
    ComputerToolInput input = ParseOk(WithTimeout("\"action\":\"key\",\"text\":\"a\",\"hold_seconds\":" + holdSeconds));
    Assert.Equal(holdSeconds, input.HoldSeconds);
  }
}
