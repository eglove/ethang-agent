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
}
