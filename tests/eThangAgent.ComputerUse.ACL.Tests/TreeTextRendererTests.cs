namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Golden renderer contract (the batch brief, task 14): full pages, delta
///     summaries, the no_change sentence, priority trimming with sparse indices and
///     re-added ancestors, the surface line, value truncation, newline flattening,
///     and the flag order. Every expected string is pinned verbatim.</summary>
public class TreeTextRendererTests
{
  private static readonly CaptureAppApp Notepad =
      new(1234, "Notepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", "Notepad.exe");

  private static readonly CaptureAppWindow MainWindow =
      new("Untitled - Notepad", 77, [10, 20, 800, 600], "window", "stable");

  private static CaptureAppElement El(int index, string role, string kind, string? title = null,
      string? value = null, int[]? bounds = null, bool enabled = true, bool editable = false,
      string[]? actions = null, bool focused = false, bool selected = false, bool pressable = false,
      bool hasMenu = false) => new(index, role, kind, title, value, bounds ?? [0, 0, 10, 10],
      enabled, editable, actions ?? [], focused, selected, pressable, hasMenu, null, null, null);

  private static CaptureAppResult Capture(string stateId, string mode, string? baseStateId,
      CaptureAppWindow window, params CaptureAppElement[] elements) =>
      new(stateId, mode, baseStateId, Notepad, window, elements, null, null);

  private static string Lines(params string[] lines) => string.Join("\n", lines);

  [Fact]
  public void Full_RendersHeaderRowsFlagsAndActions_InCanonicalShape()
  {
    CaptureAppResult capture = Capture("s-1", "full", null, MainWindow,
        El(0, "window", "window", "Untitled - Notepad", bounds: [10, 20, 800, 600], hasMenu: true),
        El(1, "pane", "pane", "", bounds: [12, 22, 796, 560]),
        El(2, "button", "button", "OK", bounds: [15, 30, 60, 24], actions: ["press"], pressable: true),
        El(3, "edit", "edit", "Name", "Ada", [15, 60, 120, 24], editable: true, focused: true),
        El(4, "edit", "edit", "Log", "0123456789012345678901234567890123456789012345678901234567890123456789",
            [15, 90, 120, 24], editable: true),
        El(5, "button", "button", "Multi\nline", bounds: [15, 120, 60, 24],
            actions: ["press", "custom"], pressable: true),
        El(6, "listitem", "item", "Leaf", bounds: [16, 150, 80, 24],
            actions: ["expand", "collapse"], selected: true));

    string text = TreeTextRenderer.Render(capture);

    string expected = Lines(
        "state_id s-1",
        "app: Microsoft.WindowsNotepad_8wekyb3d8bbwe!App pid=1234 \"Notepad\"",
        "window: \"Untitled - Notepad\" id=77 bounds=[10,20,800,600]",
        "elements (7):",
        " [0] window \"Untitled - Notepad\" (has_menu)",
        "  [1] pane",
        "   [2] button \"OK\" (pressable default_action)",
        "   [3] edit \"Name\" = \"Ada\" (editable focused)",
        "   [4] edit \"Log\" = \"012345678901234567890123456789012345678901234567890123456789…\" (editable)",
        "   [5] button \"Multi line\" (pressable default_action) actions=[custom]",
        "   [6] listitem \"Leaf\" (selected) actions=[expand,collapse]");
    Assert.Equal(expected, text);
  }

  [Fact]
  public void Delta_RendersChangedRowsOnly_WithSnapshotHeaderAndClosingSentence()
  {
    CaptureAppResult capture = Capture("s-2", "delta", "s-1", MainWindow,
        El(3, "edit", "edit", "Name", "Ada Lovelace", [15, 60, 120, 24], editable: true, focused: true),
        El(6, "listitem", "item", "Leaf", bounds: [16, 150, 80, 24],
            actions: ["expand", "collapse"], selected: true));

    string text = TreeTextRenderer.Render(capture);

    string expected = Lines(
        "state_id s-2",
        "app: Microsoft.WindowsNotepad_8wekyb3d8bbwe!App pid=1234 \"Notepad\"",
        "window: \"Untitled - Notepad\" id=77 bounds=[10,20,800,600]",
        "ax_snapshot: mode=delta base_state_id=s-1 elements=2",
        "elements (2):",
        "~ [3] edit \"Name\" = \"Ada Lovelace\" (editable focused)",
        "~ [6] listitem \"Leaf\" (selected) actions=[expand,collapse]",
        "Unchanged rows are omitted: no element was added or removed, so element order and every index are identical to s-1 and remain valid under state_id s-2.");
    Assert.Equal(expected, text);
  }

  [Fact]
  public void NoChange_RendersHeaderAndTheSingleSentence()
  {
    CaptureAppResult capture = Capture("s-4", "no_change", "s-3", MainWindow);

    string text = TreeTextRenderer.Render(capture);

    string expected = Lines(
        "state_id s-4",
        "app: Microsoft.WindowsNotepad_8wekyb3d8bbwe!App pid=1234 \"Notepad\"",
        "window: \"Untitled - Notepad\" id=77 bounds=[10,20,800,600]",
        "No material accessibility change since s-3: the element list, its order, and every index are identical. Take the next action instead of re-observing.");
    Assert.Equal(expected, text);
  }

  [Fact]
  public void SurfaceLine_RendersForModalKinds_AndForNonStableWindows_HidesForPlainWindows()
  {
    CaptureAppResult savePanel = Capture("s-1", "full", null,
        new CaptureAppWindow("Save As", 77, [10, 20, 800, 600], "save_panel", "stable"),
        El(0, "pane", "pane", "Save As", bounds: [10, 20, 800, 600]));
    Assert.Contains("\nsurface: kind=save_panel lifecycle=stable", TreeTextRenderer.Render(savePanel), StringComparison.Ordinal);

    CaptureAppResult replaced = Capture("s-1", "full", null,
        new CaptureAppWindow("Untitled - Notepad", 77, [10, 20, 800, 600], "window", "replaced"),
        El(0, "pane", "pane", bounds: [10, 20, 800, 600]));
    Assert.Contains("\nsurface: kind=window lifecycle=replaced", TreeTextRenderer.Render(replaced), StringComparison.Ordinal);

    CaptureAppResult plain = Capture("s-1", "full", null, MainWindow,
        El(0, "pane", "pane", bounds: [10, 20, 800, 600]));
    Assert.DoesNotContain("surface:", TreeTextRenderer.Render(plain), StringComparison.Ordinal);
  }

  [Fact]
  public void Trimming_KeepsTopPriorityElements_ReaddsAncestors_RestoresIndexOrder_AndNotesSparseness()
  {
    List<CaptureAppElement> elements = [El(0, "pane", "pane", "Root", bounds: [0, 0, 2000, 4000])];
    for (int i = 1; i <= 1599; i++)
    {
      elements.Add(El(i, "button", "button", "Button " + i,
          bounds: [i % 50 * 30, i / 50 * 30, 28, 28], actions: ["press"], pressable: true));
    }

    elements.Add(El(1600, "edit", "edit", "Search", "q", [0, 990, 200, 24],
        editable: true, focused: true));
    CaptureAppResult capture = Capture("s-1", "full", null, MainWindow, [.. elements]);

    string text = TreeTextRenderer.Render(capture);

    // Buttons carrying the ubiquitous press rank 600 (pressable 200 + default_action
    // 240 + leaf 100 + title 60); the focused edit ranks 520. Top-1500 = buttons
    // 1..1500 (index ASC breaks score ties); the root pane is re-added as an
    // ancestor -> 1501 shown; hidden = buttons 1501..1599 + the edit = 100.
    Assert.StartsWith("state_id s-1", text, StringComparison.Ordinal);
    Assert.Contains("note: 1501 of 1601 elements shown (selected by priority, ancestors kept) - indices are sparse; 100 hidden.", text, StringComparison.Ordinal);
    Assert.Contains("elements (1501):", text, StringComparison.Ordinal);
    Assert.Contains(" [0] pane \"Root\"", text, StringComparison.Ordinal);
    Assert.Contains("  [1] button \"Button 1\"", text, StringComparison.Ordinal);
    Assert.Contains("  [1500] button \"Button 1500\" (pressable default_action)", text, StringComparison.Ordinal);
    Assert.DoesNotContain("[1501]", text, StringComparison.Ordinal);
    Assert.DoesNotContain("[1599]", text, StringComparison.Ordinal);
    Assert.DoesNotContain("[1600]", text, StringComparison.Ordinal);
    int button1500Pos = text.IndexOf("[1500]", StringComparison.Ordinal);
    int button1499Pos = text.IndexOf("[1499]", StringComparison.Ordinal);
    Assert.True(button1500Pos > button1499Pos, "rows must be re-sorted by index");
  }

  [Fact]
  public void Trimming_ZeroAreaElementsLose_WeightedScoringDecidesTheCut()
  {
    List<CaptureAppElement> elements =
    [
      El(0, "button", "button", "A", bounds: [0, 0, 10, 10], actions: ["press"], pressable: true),
      El(1, "button", "button", "B", bounds: [20, 0, 10, 10], actions: ["press"], pressable: true),
      El(2, "pane", "pane", "Zero", bounds: [0, 0, 0, 0]),
    ];
    CaptureAppResult capture = Capture("s-1", "full", null, MainWindow, [.. elements]);

    string text = TreeTextRenderer.Render(capture, maxElements: 2);

    Assert.Contains("note: 2 of 3 elements shown (selected by priority, ancestors kept) - indices are sparse; 1 hidden.", text, StringComparison.Ordinal);
    Assert.Contains("[0] button \"A\"", text, StringComparison.Ordinal);
    Assert.Contains("[1] button \"B\"", text, StringComparison.Ordinal);
    Assert.DoesNotContain("[2]", text, StringComparison.Ordinal);
  }

  [Fact]
  public void DisabledFlag_AndFullFlagOrder_RenderInCanonicalSequence()
  {
    CaptureAppResult capture = Capture("s-1", "full", null, MainWindow,
        El(0, "button", "button", "Broken", bounds: [0, 0, 40, 20], enabled: false,
            editable: true, actions: ["press"], pressable: true, hasMenu: true, selected: true));

    string text = TreeTextRenderer.Render(capture);

    Assert.Contains(" [0] button \"Broken\" (pressable editable has_menu selected default_action disabled)", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Depth_IsDerivedFromBoundsContainment_UpToTheTwentyFourCap()
  {
    CaptureAppResult capture = Capture("s-1", "full", null, MainWindow,
        El(0, "pane", "pane", "Root", bounds: [0, 0, 800, 600]),
        El(1, "pane", "pane", "Mid", bounds: [10, 10, 700, 500]),
        El(2, "button", "button", "Deep", bounds: [20, 20, 60, 24], actions: ["press"], pressable: true));

    string text = TreeTextRenderer.Render(capture);

    Assert.Contains(" [0] pane \"Root\"", text, StringComparison.Ordinal);
    Assert.Contains("  [1] pane \"Mid\"", text, StringComparison.Ordinal);
    Assert.Contains("   [2] button \"Deep\"", text, StringComparison.Ordinal);
  }
}
