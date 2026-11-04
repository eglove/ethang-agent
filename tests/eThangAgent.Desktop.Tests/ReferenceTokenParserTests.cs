using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

public class ReferenceTokenParserTests
{
  [Theory]
  [InlineData("look at @AgentV", 15, 8, "AgentV")]
  [InlineData("@src/Foo.cs", 11, 0, "src/Foo.cs")]
  [InlineData("a @b @src/Desk", 14, 5, "src/Desk")]
  public void FindsActiveFileToken(string input, int caret, int expectedStart, string? expectedQuery)
  {
    ReferenceToken? token = ReferenceTokenParser.FindActive(input, caret);
    Assert.NotNull(token);
    Assert.Equal(ReferenceTrigger.File, token.Trigger);
    Assert.Equal(expectedStart, token.Start);
    Assert.Equal(expectedQuery, token.Query);
    Assert.Equal(caret - expectedStart, token.Length);
  }

  [Theory]
  [InlineData("mail me at foo@bar.com", 21)]
  [InlineData("@@literal", 9)]
  [InlineData("!ls @src", 8)]
  [InlineData("plain text", 10)]
  public void ReturnsNullWhenNoTokenIsActive(string? input, int caret)
    => Assert.Null(ReferenceTokenParser.FindActive(input, caret));

  [Fact]
  public void SlashIsACommandTriggerOnlyAtInputStart()
  {
    ReferenceToken? token = ReferenceTokenParser.FindActive("/ne", 3);
    Assert.NotNull(token);
    Assert.Equal(ReferenceTrigger.Command, token.Trigger);
    Assert.Null(ReferenceTokenParser.FindActive("see /ne", 7));
  }

  [Fact]
  public void UnescapeCollapsesDoubleAt()
    => Assert.Equal("a@b", ReferenceTokenParser.Unescape("a@@b"));
}
