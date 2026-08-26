namespace eThangAgent.ToolDomain.Tests;

/// <summary>Golden-string tests pinning MarkdownRenderer output to the reference
/// markdown-generator semantics: blocks separated by blank lines, space blocks emit
/// count+1 newlines and suppress the following separator, frontmatter keys sort
/// title-first, YAML scalars quote only when containing ':' / '"' / '#'.</summary>
public class MarkdownRendererTests
{
  private static string Render(params MarkdownBlock?[] blocks) =>
      MarkdownRenderer.Render(new MarkdownDocument(blocks));

  // ---- text / separators ----

  [Fact]
  public void SingleText_RendersWithTrailingNewline() =>
      Assert.Equal("Hello\n", Render(new TextBlock("Hello")));

  [Fact]
  public void TwoBlocks_SeparatedByBlankLine() =>
      Assert.Equal("First\n\nSecond\n", Render(new TextBlock("First"), new TextBlock("Second")));

  [Fact]
  public void ThreeBlocks_EachSeparatedByBlankLine() =>
      Assert.Equal("A\n\nB\n\nC\n",
          Render(new TextBlock("A"), new TextBlock("B"), new TextBlock("C")));

  [Fact]
  public void EmptyDocument_RendersEmptyString() =>
      Assert.Equal(string.Empty, Render());

  [Fact]
  public void NullBlocks_AreSkipped() =>
      Assert.Equal("A\n\nB\n", Render(new TextBlock("A"), null, new TextBlock("B")));

  // ---- space ----

  [Fact]
  public void DefaultSpace_EmitsTwoNewlines_NoExtraSeparatorAfter() =>
    // space(count=1) emits count+1 = 2 newlines; the next block gets no additional blank line.
    Assert.Equal("A\n\nB\n", Render(new TextBlock("A"), new SpaceBlock(), new TextBlock("B")));

  [Fact]
  public void SpaceCount_EmitsCountPlusOneNewlines() => Assert.Equal("A\n\n\nB\n", Render(new TextBlock("A"), new SpaceBlock(2), new TextBlock("B")));

  [Fact]
  public void LeadingSpace_NoSeparatorBeforeFirstBlock() =>
      Assert.Equal("\n\nA\n", Render(new SpaceBlock(), new TextBlock("A")));

  [Fact]
  public void TrailingSpace_KeepsItsNewlines() =>
      Assert.Equal("A\n\n", Render(new TextBlock("A"), new SpaceBlock()));

  [Fact]
  public void ConsecutiveSpaces_Accumulate()
  {
    // space(1) emits 2 newlines, space(3) emits 4 — six between A and B.
    Assert.Equal("A\n\n\n\n\n\nB\n",
        Render(new TextBlock("A"), new SpaceBlock(), new SpaceBlock(3), new TextBlock("B")));
  }

  // ---- headers ----

  [Fact]
  public void Headers_AllLevels()
  {
    Assert.Equal("# T\n\n## S\n\n### D\n",
        Render(new HeaderBlock(1, "T"), new HeaderBlock(2, "S"), new HeaderBlock(3, "D")));
  }

  // ---- code ----

  [Fact]
  public void CodeBlock_WithoutLanguage_EmptyFenceInfo() =>
      Assert.Equal("```\nvar x = 1;\n```\n", Render(new CodeBlock("var x = 1;")));

  [Fact]
  public void CodeBlock_WithLanguage() =>
      Assert.Equal("```csharp\nvar x = 1;\n```\n",
          Render(new CodeBlock("var x = 1;", "csharp")));

  // ---- quotes and alerts ----

  [Fact]
  public void Quote_PrefixesEachLine() =>
      Assert.Equal("> l1\n> l2\n", Render(new QuoteBlock("l1\nl2")));

  [Fact]
  public void Alert_RendersTypeLineThenQuotedBody() =>
      Assert.Equal("> [!WARNING]\n> be careful\n",
          Render(new AlertBlock(AlertType.Warning, "be careful")));

  [Fact]
  public void Alert_MultiLineBody_EachLineQuoted() =>
      Assert.Equal("> [!TIP]\n> l1\n> l2\n",
          Render(new AlertBlock(AlertType.Tip, "l1\nl2")));

  [Theory]
  [InlineData(AlertType.Caution, "CAUTION")]
  [InlineData(AlertType.Important, "IMPORTANT")]
  [InlineData(AlertType.Note, "NOTE")]
  [InlineData(AlertType.Tip, "TIP")]
  [InlineData(AlertType.Warning, "WARNING")]
  public void Alert_TypesRenderUppercase(AlertType type, string expected) =>
      Assert.Equal("> [!" + expected + "]\n> x\n", Render(new AlertBlock(type, "x")));

  // ---- lists ----

  [Fact]
  public void UnorderedList_FlatItems() =>
      Assert.Equal("* a\n* b\n", Render(new ListBlock(ListKind.Unordered,
          [new ListItem("a"), new ListItem("b")])));

  [Fact]
  public void NumberedList_UsesLiteralOneDotPrefix() =>
      Assert.Equal("1. a\n1. b\n", Render(new ListBlock(ListKind.Numbered,
          [new ListItem("a"), new ListItem("b")])));

  [Fact]
  public void UnorderedList_NestedChildren_TabIndented() =>
      Assert.Equal("* p\n\t* c1\n\t* c2\n",
          Render(new ListBlock(ListKind.Unordered,
              [new ListItem("p", [new ListItem("c1"), new ListItem("c2")])])));

  [Fact]
  public void UnorderedList_DepthTwoNesting_DoubleTabIndent() =>
      Assert.Equal("* p\n\t* c\n\t\t* g\n",
          Render(new ListBlock(ListKind.Unordered,
              [new ListItem("p", [new ListItem("c", [new ListItem("g")])])])));

  [Fact]
  public void NumberedList_NestedChildren_TabIndented() =>
      Assert.Equal("1. p\n\t1. c\n",
          Render(new ListBlock(ListKind.Numbered, [new ListItem("p", [new ListItem("c")])])));

  // ---- task list ----

  [Fact]
  public void TaskList_CheckboxStates() =>
      Assert.Equal("[X] ship\n[ ] wait\n",
          Render(new TaskListBlock([new TaskListItem(true, "ship"), new TaskListItem(false, "wait")])));

  // ---- tables ----

  [Fact]
  public void Table_PlainHeaders_DefaultDividers()
  {
    string doc = Render(new TableBlock(
        [new TableHeader("Name"), new TableHeader("Qty")],
        [["a", "1"], ["b", "2"]]));
    Assert.Equal("| Name | Qty |\n| --- | --- |\n| a | 1 |\n| b | 2 |\n", doc);
  }

  [Fact]
  public void Table_AlignmentDividers()
  {
    string doc = Render(new TableBlock(
    [
        new TableHeader("L", TableAlign.Left),
            new TableHeader("C", TableAlign.Center),
            new TableHeader("R", TableAlign.Right),
            new TableHeader("P"),
        ], [["1", "2", "3", "4"]]));
    Assert.Equal("| L | C | R | P |\n| :--- | :---: | ---: | --- |\n| 1 | 2 | 3 | 4 |\n", doc);
  }

  // ---- frontmatter ----

  [Fact]
  public void FrontMatter_TitleSortsFirst_OthersSorted_BlocksFollow()
  {
    string doc = MarkdownRenderer.Render(new MarkdownDocument(
        [new TextBlock("Body")],
        new Dictionary<string, object> { ["title"] = "T", ["b"] = "x", ["a"] = true, ["n"] = 1.5 }));
    Assert.Equal("---\ntitle: T\na: true\nb: x\nn: 1.5\n---\nBody\n", doc);
  }

  [Fact]
  public void FrontMatter_PlainScalar_Unquoted()
  {
    string doc = MarkdownRenderer.Render(new MarkdownDocument([], new Dictionary<string, object> { ["k"] = "v" }));
    Assert.Equal("---\nk: v\n---\n", doc);
  }

  [Theory]
  [InlineData("Hello: world", "\"Hello: world\"")]
  [InlineData("has # hash", "\"has # hash\"")]
  [InlineData("a\"b\\c", "\"a\\\"b\\\\c\"")]
  public void FrontMatter_SpecialScalars_AreQuotedAndEscaped(string value, string expected)
  {
    string doc = MarkdownRenderer.Render(new MarkdownDocument([], new Dictionary<string, object> { ["k"] = value }));
    Assert.Equal("---\nk: " + expected + "\n---\n", doc);
  }

  [Fact]
  public void FrontMatter_IntegerNumber_FormatsWithoutDecimalPoint()
  {
    string doc = MarkdownRenderer.Render(new MarkdownDocument([], new Dictionary<string, object> { ["weight"] = 80.0 }));
    Assert.Equal("---\nweight: 80\n---\n", doc);
  }
}
