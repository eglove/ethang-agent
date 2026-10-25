namespace eThangAgent.ToolDomain.Tests;

// Issue #283 acceptance: the rendered line is the model-facing contract, so it is pinned
// verbatim - including the zero case, which is the one that catches a no-op replace.
public class WorkspaceChangeSetRenderTests
{
  private static WorkspaceChangeSet Changes(
      string[]? added = null, string[]? modified = null, string[]? removed = null,
      bool complete = true) =>
      new(added ?? [], modified ?? [], removed ?? [], complete);

  [Fact]
  public void Render_NothingChanged_SaysSoExplicitly() =>
      Assert.Equal(
          "[exec: modified 0 workspace files — the program completed but changed nothing]",
          Changes().Render());

  [Fact]
  public void Render_OneFile_UsesSingular() =>
      Assert.Equal("[exec: modified 1 workspace file — src/a.cs]",
          Changes(modified: ["src/a.cs"]).Render());

  [Fact]
  public void Render_MultipleFiles_ListsThem() =>
      Assert.Equal("[exec: modified 2 workspace files — src/a.cs, src/b.cs]",
          Changes(modified: ["src/a.cs", "src/b.cs"]).Render());

  [Fact]
  public void Render_MarksAddedAndRemoved() =>
      Assert.Equal(
          "[exec: modified 3 workspace files — src/kept.cs, src/new.cs (added), src/gone.cs (removed)]",
          Changes(added: ["src/new.cs"], modified: ["src/kept.cs"], removed: ["src/gone.cs"]).Render());

  // A refactor touching hundreds of files must not flood the transcript, and the
  // remainder must be COUNTED - a silent drop would read as a complete list.
  [Fact]
  public void Render_ManyFiles_TruncatesAndCountsTheRemainder()
  {
    string[] many = [.. Enumerable.Range(0, 30).Select(i => "src/f" + i + ".cs")];

    string rendered = Changes(modified: many).Render();

    Assert.Contains("modified 30 workspace files", rendered, StringComparison.Ordinal);
    Assert.Contains(", and 18 more", rendered, StringComparison.Ordinal);
  }

  // A truncated walk is not a complete account, and the line must never imply it is.
  [Fact]
  public void Render_TruncatedWalk_NamesTheLimit()
  {
    string rendered = Changes(modified: ["src/a.cs"], complete: false).Render();

    Assert.Contains("truncated", rendered, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_TruncatedWalkWithNoChanges_StillSaysSo()
  {
    string rendered = Changes(complete: false).Render();

    Assert.Contains("modified 0 workspace files", rendered, StringComparison.Ordinal);
    Assert.Contains("not a complete account", rendered, StringComparison.Ordinal);
  }
}
