namespace eThangAgent.ToolDomain;

/// <summary>A line-ending convention. A bare CR (backslash-r alone) is treated as a
///     CR-terminated line and normalized to the target convention rather than
///     preserved: no project in this ecosystem declares bare CR, and carrying it
///     forward would keep a third, unnameable convention alive inside a file.</summary>
public enum LineEnding { Lf, CrLf }
