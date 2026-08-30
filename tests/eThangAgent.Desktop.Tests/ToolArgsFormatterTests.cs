using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

public class ToolArgsFormatterTests
{
  [Fact]
  public void Compact_Json_Object_Is_Indented()
  {
    string formatted = ToolArgsFormatter.Indent(/*lang=json,strict*/ "{\"path\":\"a.cs\",\"start\":1}");
    Assert.Contains("\"path\": \"a.cs\"", formatted, StringComparison.Ordinal);
    Assert.Contains('\n', formatted);
  }

  [Fact]
  public void Non_Json_Input_Returns_Raw()
  {
    const string raw = "just some text";
    Assert.Equal(raw, ToolArgsFormatter.Indent(raw));
  }

  [Fact]
  public void Display_Preview_Is_Single_Line_Truncated()
  {
    string preview = ToolArgsFormatter.Preview(/*lang=json,strict*/ "{\"a\":1}", maxChars: 8);
    Assert.True(preview.Length <= 9);
    Assert.DoesNotContain('\n', preview);
  }
}
