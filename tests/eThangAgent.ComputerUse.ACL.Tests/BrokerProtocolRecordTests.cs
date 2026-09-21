namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Typed wire records (spec 7): JSON fixtures round-trip capture_app results and
///     action receipts field-for-field, and wrong shapes fail closed.</summary>
public class BrokerProtocolRecordTests
{
  private static readonly string D = char.ToString((char)34);

  private static string J(string name) => D + name + D;

  [Fact]
  public void CaptureAppResult_Fixture_RoundTripsEveryField()
  {
    string element = "{" + J("index") + ":0," + J("role") + ":" + J("document") + "," + J("kind") + ":" + J("pane")
      + "," + J("title") + ":" + J("text editor") + "," + J("value") + ":" + J("hello")
      + "," + J("bounds") + ":[8,31,997,756]," + J("enabled") + ":true," + J("editable") + ":true"
      + "," + J("actions") + ":[" + J("press") + "]," + J("focused") + ":true," + J("selected") + ":false"
      + "," + J("pressable") + ":true," + J("has_menu") + ":false," + J("children_total") + ":3"
      + "," + J("children_shown") + ":3," + J("children_offset") + ":0}";
    string screenshot = "{" + J("data") + ":" + J("aGk=") + "," + J("width") + ":320," + J("height") + ":240"
      + "," + J("pointer") + ":{" + J("x") + ":160," + J("y") + ":120," + J("w") + ":32," + J("h") + ":32},"
      + J("blank") + ":false}";
    string app = "{" + J("pid") + ":4172," + J("name") + ":" + J("Notepad") + "," + J("aumid") + ":" + J("{MICROSOFT}_notepad") + "," + J("exe") + ":" + J("notepad.exe") + "}";
    string window = "{" + J("title") + ":" + J("Untitled - Notepad") + "," + J("window_id") + ":197," + J("bounds") + ":[8,31,1013,787],"
      + J("surface_kind") + ":" + J("window") + "," + J("surface_lifecycle") + ":" + J("stable") + "}";
    string fixture = "{" + J("id") + ":9," + J("result") + ":{" + J("state_id") + ":" + J("s-41") + "," + J("snapshot_mode") + ":" + J("delta")
      + "," + J("base_state_id") + ":" + J("s-40") + "," + J("app") + ":" + app + "," + J("window") + ":" + window + "," + J("elements") + ":[" + element + "]," + J("screenshot") + ":" + screenshot + "," + J("effect_evidence") + ":" + J("unchanged") + "}}";
    BrokerReply reply = BrokerReply.Parse(fixture) ?? throw new InvalidOperationException("parse failed");
    CaptureAppResult? parsed = CaptureAppResult.From(reply);
    Assert.NotNull(parsed);
    Assert.Equal("s-41", parsed.StateId);
    Assert.Equal("delta", parsed.SnapshotMode);
    Assert.Equal("s-40", parsed.BaseStateId);
    Assert.Equal(4172, parsed.App.Pid);
    Assert.Equal("Notepad", parsed.App.Name);
    Assert.Equal("{MICROSOFT}_notepad", parsed.App.Aumid);
    Assert.Equal("notepad.exe", parsed.App.Exe);
    Assert.Equal("Untitled - Notepad", parsed.Window.Title);
    Assert.Equal(197, parsed.Window.WindowId);
    Assert.Equal([8, 31, 1013, 787], parsed.Window.Bounds);
    Assert.Equal("window", parsed.Window.SurfaceKind);
    Assert.Equal("stable", parsed.Window.SurfaceLifecycle);
    CaptureAppElement element2 = Assert.Single(parsed.Elements);
    Assert.Equal(0, element2.Index);
    Assert.Equal("document", element2.Role);
    Assert.Equal("pane", element2.Kind);
    Assert.Equal("text editor", element2.Title);
    Assert.Equal("hello", element2.Value);
    Assert.Equal([8, 31, 997, 756], element2.Bounds);
    Assert.True(element2.Enabled);
    Assert.True(element2.Editable);
    Assert.Equal(["press"], element2.Actions);
    Assert.True(element2.Focused);
    Assert.False(element2.Selected);
    Assert.True(element2.Pressable);
    Assert.False(element2.HasMenu);
    Assert.Equal(3, element2.ChildrenTotal);
    Assert.Equal(3, element2.ChildrenShown);
    Assert.Equal(0, element2.ChildrenOffset);
    Assert.NotNull(parsed.Screenshot);
    Assert.Equal("aGk=", parsed.Screenshot.Data);
    Assert.Equal(320, parsed.Screenshot.Width);
    Assert.Equal(240, parsed.Screenshot.Height);
    Assert.Equal(160, parsed.Screenshot.PointerRect?.X);
    Assert.Equal(120, parsed.Screenshot.PointerRect?.Y);
    Assert.Equal(32, parsed.Screenshot.PointerRect?.W);
    Assert.Equal(32, parsed.Screenshot.PointerRect?.H);
    Assert.False(parsed.Screenshot.Blank);
    Assert.Equal("unchanged", parsed.EffectEvidence);
  }

  [Fact]
  public void CaptureAppResult_Minimal_Fixture_OptionalsNull()
  {
    string app = "{" + J("pid") + ":5," + J("name") + ":" + J("n") + "," + J("aumid") + ":null," + J("exe") + ":null}";
    string window = "{" + J("title") + ":" + J("t") + "," + J("window_id") + ":1," + J("bounds") + ":[0,0,1,1],"
      + J("surface_kind") + ":" + J("window") + "," + J("surface_lifecycle") + ":" + J("stable") + "}";
    string fixture = "{" + J("id") + ":1," + J("result") + ":{" + J("state_id") + ":" + J("s-1") + "," + J("snapshot_mode") + ":" + J("full")
      + "," + J("app") + ":" + app + "," + J("window") + ":" + window + "," + J("elements") + ":[]}}";
    BrokerReply reply = BrokerReply.Parse(fixture) ?? throw new InvalidOperationException("parse failed");
    CaptureAppResult parsed = CaptureAppResult.From(reply)!;
    Assert.Null(parsed.BaseStateId);
    Assert.Null(parsed.Screenshot);
    Assert.Null(parsed.EffectEvidence);
    Assert.Empty(parsed.Elements);
  }

  [Fact]
  public void CaptureAppResult_FromWrongShape_FailsNull()
  {
    string fixture = "{" + J("id") + ":2," + J("result") + ":{" + J("echo") + ":true}}";
    BrokerReply reply = BrokerReply.Parse(fixture) ?? throw new InvalidOperationException("parse failed");
    Assert.Null(CaptureAppResult.From(reply));
  }

  [Fact]
  public void CaptureAppResult_UnknownSnapshotMode_FailsClosed()
  {
    string app = "{" + J("pid") + ":5," + J("name") + ":" + J("n") + "}";
    string window = "{" + J("title") + ":" + J("t") + "," + J("window_id") + ":1," + J("bounds") + ":[0,0,1,1],"
      + J("surface_kind") + ":" + J("window") + "," + J("surface_lifecycle") + ":" + J("stable") + "}";
    string fixture = "{" + J("id") + ":7," + J("result") + ":{" + J("state_id") + ":" + J("s-1") + "," + J("snapshot_mode") + ":" + J("upside_down")
      + "," + J("app") + ":" + app + "," + J("window") + ":" + window + "," + J("elements") + ":[]}}";
    BrokerReply reply = BrokerReply.Parse(fixture) ?? throw new InvalidOperationException("parse failed");
    Assert.Null(CaptureAppResult.From(reply));
  }

  [Fact]
  public void ActionReceipt_Fixture_RoundTripsAndHonorsHonesty()
  {
    string fixture = "{" + J("id") + ":3," + J("result") + ":{" + J("action_sent") + ":false," + J("dispatch_status") + ":" + J("possibly_sent")
      + "," + J("effect_evidence") + ":" + J("unchanged") + "}}";
    BrokerReply reply = BrokerReply.Parse(fixture) ?? throw new InvalidOperationException("parse failed");
    BrokerActionReceipt? receipt = BrokerActionReceipt.From(reply);
    Assert.NotNull(receipt);
    Assert.False(receipt.ActionSent);
    Assert.Equal("possibly_sent", receipt.DispatchStatus);
    Assert.Equal("unchanged", receipt.EffectEvidence);
  }

  [Fact]
  public void ActionReceipt_FromWrongShape_FailsNull()
  {
    string fixture = "{" + J("id") + ":4," + J("result") + ":{" + J("stub") + ":true}}";
    BrokerReply reply = BrokerReply.Parse(fixture) ?? throw new InvalidOperationException("parse failed");
    Assert.Null(BrokerActionReceipt.From(reply));
  }
}
