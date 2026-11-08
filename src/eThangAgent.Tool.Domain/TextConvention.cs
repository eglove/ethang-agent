namespace eThangAgent.ToolDomain;

/// <summary>The textual conventions a write lands in: the line ending every terminator is
/// rewritten to, and whether the text carries a final newline. <see cref="InsertFinalNewline"/>
/// is the editorconfig <c>insert_final_newline</c> setting — false writes the text verbatim in
/// that respect, so a file may deliberately end without a terminator.</summary>
public readonly record struct TextConvention(LineEnding Ending, bool InsertFinalNewline);
