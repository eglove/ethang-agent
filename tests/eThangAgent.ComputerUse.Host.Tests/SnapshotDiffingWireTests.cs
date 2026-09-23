using System.Text.Json;
using System.Windows.Automation;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix round 5 (F9): the HOST computes snapshot_mode from its own
///     per-window last-accepted walk table - first walk or disable_diffing => full;
///     identical table (count+roles+titles+values) => no_change with base_state_id
///     echoing the previous state id; changed subset => delta naming the changed
///     rows; structural change => full. Delta rows stay COMPLETE (the ACL parser
///     fails closed on partial rows); no_change drops the table - the renderer's
///     single-sentence page is the client contract.</summary>

public class SnapshotDiffingWireTests

{
  private static readonly string[] WireKeys =
  [
    "index", "role", "kind", "title", "value", "bounds", "enabled", "editable",
    "actions", "focused", "selected", "pressable", "has_menu",
  ];

  [Fact]
  public void FirstWalk_IsFull()
  {
    SteadyStateObserver observer = new();
    BrokerResponse first = Capture(observer);
    Assert.Null(first.Error);
    Assert.Equal("full", SnapshotMode(first));
  }

  [Fact]
  public void UnchangedSecondWalk_IsNoChange_EchoingBaseStateId()
  {
    SteadyStateObserver observer = new();
    BrokerResponse first = Capture(observer);
    string firstStateId = StateId(first);
    BrokerResponse second = Capture(observer);
    Assert.Null(second.Error);
    Assert.Equal("no_change", SnapshotMode(second));
    Assert.Equal(firstStateId, BaseStateId(second));
  }

  [Fact]
  public void ChangedValue_IsDelta_NamingTheChangedRow()
  {
    SteadyStateObserver observer = new();
    _ = Capture(observer);
    observer.Mutate("ed-1", "typed text");
    BrokerResponse third = Capture(observer);
    Assert.Null(third.Error);
    Assert.Equal("delta", SnapshotMode(third));
    JsonElement elements = Elements(third);
    Assert.Equal(1, elements.GetArrayLength());
    JsonElement row = elements[0];
    Assert.Equal("ed-1", row.GetProperty("title").GetString());
    Assert.Equal("typed text", row.GetProperty("value").GetString());
  }

  [Fact]
  public void StructuralChange_IsFull()
  {
    SteadyStateObserver observer = new();
    _ = Capture(observer);
    observer.RemoveElement("btn-1");
    BrokerResponse third = Capture(observer);
    Assert.Null(third.Error);
    Assert.Equal("full", SnapshotMode(third));
    Assert.Equal(1, Elements(third).GetArrayLength());
  }

  [Fact]
  public void DisableDiffing_ForcesFull_AndAdvancesTheBaseline()
  {
    SteadyStateObserver observer = new();
    _ = Capture(observer);
    BrokerResponse forced = Capture(observer, disableDiffing: true);
    Assert.Equal("full", SnapshotMode(forced));
    // The forced full IS the new baseline: an unchanged next walk is no_change.
    BrokerResponse after = Capture(observer);
    Assert.Equal("no_change", SnapshotMode(after));
  }

  [Fact]
  public void Elements_AreCompleteInDeltaMode()
  {
    // Delta rows carry the FULL element shape, not a partial patch - the ACL
    // parser fails closed on incomplete rows.
    SteadyStateObserver observer = new();
    _ = Capture(observer);
    observer.Mutate("ed-1", "new value");
    BrokerResponse delta = Capture(observer);
    JsonElement row = Elements(delta)[0];
    foreach (string key in WireKeys)
    {
      Assert.True(row.TryGetProperty(key, out _), key + " must travel on a delta row");
    }
  }

  private static BrokerResponse Capture(SteadyStateObserver observer, bool disableDiffing = false)
  {
    PipeServer server = new(new BrokerConfig("ignored-pipe", "t"), observer);
    _ = server.Dispatch(0, "authenticate", TokenJson().RootElement, connectionId: 1);
    string snapshotMode = disableDiffing ? "force_full" : "auto";
    string parameters = "{" + Quote("app_ref") + ":{" + Quote("pid") + ":1}," + Quote("snapshot_mode") + ":" + Quote(snapshotMode) + "}";
    return server.Dispatch(2, "capture_app", JsonDocument.Parse(parameters).RootElement, connectionId: 1);
  }

  private static string Quote(string name) => "\"" + name + "\"";

  private static JsonDocument TokenJson()
  {
    string frame = "{" + Quote("token") + ":" + Quote("t") + "}";
    return JsonDocument.Parse(frame);
  }

  private static string SnapshotMode(BrokerResponse reply) => reply.Result!.Value.GetProperty("snapshot_mode").GetString()!;

  private static string StateId(BrokerResponse reply) => reply.Result!.Value.GetProperty("state_id").GetString()!;

  private static string BaseStateId(BrokerResponse reply) => reply.Result!.Value.GetProperty("base_state_id").GetString()!;

  private static JsonElement Elements(BrokerResponse reply) => reply.Result!.Value.GetProperty("elements");
}

/// <summary>A two-element tree that never changes unless the test mutates it: the
///     deterministic diffing substrate. Rows match the managed walk's wire shape</summary>
internal sealed class SteadyStateObserver : RealBrokerObserver
{
  private static readonly int[] Bounds = [0, 0, 10, 10];

  private sealed record Row(string Id, string Role, string Value);
  private readonly List<Row> _rows =
  [
    new("btn-1", "button", ""),
    new("ed-1", "edit", ""),
  ];

  public void Mutate(string id, string value) =>
    _rows[_rows.FindIndex(r => r.Id == id)] = new Row(id, _rows[_rows.FindIndex(r => r.Id == id)].Role, value);

  public void RemoveElement(string id) => _rows.RemoveAll(r => r.Id == id);

  internal override bool TryResolveAppRef(int pid, string? name, string? aumid, int? windowId, out AppRefResolution resolution, out AppRefResolutionFailure? failure)
  {
    resolution = new AppRefResolution(0x4321, pid);
    failure = null;
    return true;
  }

  internal override WalkResult? WalkElements(nint window)
  {
    List<object> rows = [];
    List<AutomationElement> cache = [];
    for (int i = 0; i < _rows.Count; i++)
    {
      Row row = _rows[i];
      rows.Add(new
      {
        index = i,
        role = row.Role,
        kind = "control",
        title = row.Id,
        value = row.Value,
        bounds = Bounds,
        enabled = true,
        editable = row.Role == "edit",
        actions = Array.Empty<string>(),
        focused = false,
        selected = false,
        pressable = row.Role == "button",
        has_menu = false,
        children_total = (int?)null,
        children_shown = (int?)null,
        children_offset = (int?)null,
      });
      cache.Add(null!); // the cache mirrors rows 1:1; rows never resolve in these tests
    }

    return new WalkResult(rows, cache);
  }
}
