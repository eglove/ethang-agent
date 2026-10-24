using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecParseHintsTests
{
  [Fact]
  public void Four_Or_More_Quotes_Produce_Delimiter_Conflict_Hint()
  {
    string program = "var s = " + new string('"', 4) + "done" + new string('"', 4) + ";";
    IReadOnlyList<string> hints = ExecParseHints.Analyze(program);
    Assert.Contains(hints, h => h.Contains("3 quote", StringComparison.Ordinal));
  }

  [Fact]
  public void Opening_Delimiter_With_Content_On_The_Same_Line_Produces_Opening_Hint()
  {
    IReadOnlyList<string> hints = ExecParseHints.Analyze("var s = \"\"\"broken");
    Assert.Contains(hints, h => h.Contains("followed by a newline", StringComparison.Ordinal));
  }

  [Fact]
  public void Closing_Delimiter_Not_At_Line_Start_Produces_Closing_Hint()
  {
    const string program = "var s = \"\"\"\ntext\n    \"\"\"";
    IReadOnlyList<string> hints = ExecParseHints.Analyze(program);
    Assert.Contains(hints, h => h.Contains("start its own line", StringComparison.Ordinal));
  }

  [Fact]
  public void Clean_Program_Produces_No_Hints() =>
      Assert.Empty(ExecParseHints.Analyze("var x = 1;\nreturn x;"));

  [Fact]
  public void Wellformed_Raw_String_Produces_No_Hints()
  {
    const string program = "var s = \"\"\"\nline one\n\"\"\";";
    Assert.Empty(ExecParseHints.Analyze(program));
  }

  // Issue #260: the hints named the DIAGNOSIS but never the REMEDY, so a model that
  // hit a raw-string parse failure rediscovered the write tool's `lines` parameter
  // from scratch. Every delimiter hint must now name the remedy alongside the rule.
  [Fact]
  public void Delimiter_Conflict_Hint_Names_The_Lines_Remedy()
  {
    string program = "var s = " + new string('"', 4) + "done" + new string('"', 4) + ";";
    IReadOnlyList<string> hints = ExecParseHints.Analyze(program);
    Assert.Contains(hints, h => h.Contains("`lines`", StringComparison.Ordinal) && h.Contains("write tool", StringComparison.Ordinal));
  }

  [Fact]
  public void Opening_Delimiter_Hint_Names_The_Lines_Remedy()
  {
    IReadOnlyList<string> hints = ExecParseHints.Analyze("var s = \"\"\"broken");
    Assert.Contains(hints, h => h.Contains("followed by a newline", StringComparison.Ordinal) && h.Contains("`lines`", StringComparison.Ordinal));
  }

  [Fact]
  public void Closing_Delimiter_Hint_Names_The_Lines_Remedy()
  {
    const string program = "var s = \"\"\"\ntext\n    \"\"\"";
    IReadOnlyList<string> hints = ExecParseHints.Analyze(program);
    Assert.Contains(hints, h => h.Contains("start its own line", StringComparison.Ordinal) && h.Contains("`lines`", StringComparison.Ordinal));
  }

  // The remedy must be the tool-domain primitive, not the in-script array-join
  // workaround the hints used to prescribe (issue #260's whole point).
  [Fact]
  public void Hints_Do_Not_Prescribe_The_InScript_Array_Workaround()
  {
    string program = "var s = " + new string('"', 4) + "done" + new string('"', 4) + ";";
    IReadOnlyList<string> hints = ExecParseHints.Analyze(program);
    Assert.DoesNotContain(hints, h => h.Contains("string array joined with newlines", StringComparison.Ordinal));
  }
}
