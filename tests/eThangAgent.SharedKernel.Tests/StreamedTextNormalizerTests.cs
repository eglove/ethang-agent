namespace eThangAgent.SharedKernel.Tests;

// Streamed reasoning arrives as tiny fragments full of hard wraps and blank-line
// runs. The normalizer makes it readable: wraps between two letters join (the
// model hard-wraps inside CamelCase identifiers constantly), wraps before
// closing/comma punctuation attach directly, real sentence breaks stay,
// bullet/heading breaks stay, and blank-line floods collapse to one blank line.
public class StreamedTextNormalizerTests
{
  [Fact]
  public void MidWord_HardWrap_JoinsWithoutSpace()
  {
    StreamedTextNormalizer n = new();
    n.Append("think");
    n.Append("ing");
    Assert.Equal("thinking", n.Text);
  }

  [Fact]
  public void CamelCaseIdentifier_HardWrap_JoinsWithoutSpace()
  {
    StreamedTextNormalizer n = new();
    n.Append("SendMessageCommandHandler, Agent");
    n.Append("\nId, RootSessionLifecycle usage");
    Assert.Equal("SendMessageCommandHandler, AgentId, RootSessionLifecycle usage", n.Text);
  }

  [Fact]
  public void NewlineAfterComma_BecomesSpace()
  {
    StreamedTextNormalizer n = new();
    n.Append("however,");
    n.Append("\nthe answer");
    Assert.Equal("however, the answer", n.Text);
  }

  [Fact]
  public void NewlineBeforeComma_JoinsDirectly()
  {
    StreamedTextNormalizer n = new();
    n.Append("consider options");
    n.Append("\n, such as caching");
    Assert.Equal("consider options, such as caching", n.Text);
  }

  [Fact]
  public void NewlineBeforeClosingParen_JoinsDirectly()
  {
    StreamedTextNormalizer n = new();
    n.Append("(see the handler above");
    n.Append("\n)");
    Assert.Equal("(see the handler above)", n.Text);
  }

  [Fact]
  public void OpeningParen_AfterNewline_GetsASpace()
  {
    StreamedTextNormalizer n = new();
    n.Append("the Conversation");
    n.Append("\n( aggregate root)");
    Assert.Equal("the Conversation ( aggregate root)", n.Text);
  }

  [Fact]
  public void SentenceBreak_IsPreserved()
  {
    StreamedTextNormalizer n = new();
    n.Append("done.\nNext point");
    Assert.Equal("done.\nNext point", n.Text);
  }

  [Fact]
  public void ColonBeforeText_JoinsWithSpace()
  {
    // Code-dense reasoning ends clauses with colons constantly; a bare wrap
    // there is a hard wrap, not structure.
    StreamedTextNormalizer n = new();
    n.Append("plan:");
    n.Append("\nfirst step");
    Assert.Equal("plan: first step", n.Text);
  }

  [Fact]
  public void ColonBeforeCapital_IsPreserved()
  {
    // A capital item on the next line reads as a heading/list entry.
    StreamedTextNormalizer n = new();
    n.Append("usage:");
    n.Append("\nAgentId handles identity");
    Assert.Equal("usage:\nAgentId handles identity", n.Text);
  }

  [Fact]
  public void BlankLineRun_CollapsesToOneBlankLine()
  {
    StreamedTextNormalizer n = new();
    n.Append("step one\n\n\n\n\n\nstep two");
    Assert.Equal("step one\n\nstep two", n.Text);
  }

  [Fact]
  public void LeadingNewlines_AreDropped()
  {
    StreamedTextNormalizer n = new();
    n.Append("\n\n\nhello");
    Assert.Equal("hello", n.Text);
  }

  [Fact]
  public void TrailingNewlines_AreTrimmedInText()
  {
    StreamedTextNormalizer n = new();
    n.Append("hello\n\n");
    Assert.Equal("hello", n.Text);
  }

  [Fact]
  public void CarriageReturns_AreIgnored()
  {
    StreamedTextNormalizer n = new();
    n.Append("end.\r\nNew thought");
    Assert.Equal("end.\nNew thought", n.Text);
  }

  [Fact]
  public void UnpunctuatedWrap_BeforeCapital_Joins_KnownTradeOff()
  {
    // A bare wrap between two letters is joined even across a capital: the
    // model's identifier wraps vastly outnumber unpunctuated sentence
    // boundaries, and a wrong join is cheaper to read than a wrong break
    // ("AgentId" vs "workingNow").
    StreamedTextNormalizer n = new();
    n.Append("still working");
    n.Append("\nNow switch");
    Assert.Equal("still workingNow switch", n.Text);
  }

  [Fact]
  public void Capital_AfterNonLetter_IsPreserved()
  {
    StreamedTextNormalizer n = new();
    n.Append("step 2");
    n.Append("\nNow check the result");
    Assert.Equal("step 2\nNow check the result", n.Text);
  }

  [Fact]
  public void BulletAfterNewline_IsPreserved()
  {
    StreamedTextNormalizer n = new();
    n.Append("options");
    n.Append("\n- first");
    Assert.Equal("options\n- first", n.Text);
  }

  [Fact]
  public void EmptyAppend_IsNoop()
  {
    StreamedTextNormalizer n = new();
    n.Append("");
    Assert.Equal("", n.Text);
  }

  [Fact]
  public void BreakState_CarriesAcrossAppends()
  {
    StreamedTextNormalizer n = new();
    n.Append("a\n");
    n.Append("\n\nb");
    Assert.Equal("a\n\nb", n.Text);
  }

  // ---- Fence awareness (markdown code fences must survive normalization) ----

  [Fact]
  public void FenceLine_BreakAfterOpeningFence_IsPreserved()
  {
    StreamedTextNormalizer n = new();
    n.Append("```csharp");
    n.Append("\npublic class Foo");
    Assert.Equal("```csharp\npublic class Foo", n.Text);
  }

  [Fact]
  public void FenceLine_InsideFence_AllBreaksPreservedVerbatim()
  {
    StreamedTextNormalizer n = new();
    n.Append("```");
    n.Append("\ncode with\n\nblank lines\nand wraps");
    n.Append("\n```");
    Assert.Equal("```\ncode with\n\nblank lines\nand wraps\n```", n.Text);
  }

  [Fact]
  public void FenceLine_LetterWrapInsideFence_IsNotJoined()
  {
    StreamedTextNormalizer n = new();
    n.Append("```csharp");
    n.Append("\npublic");
    n.Append("\nclass Foo");
    Assert.Equal("```csharp\npublic\nclass Foo", n.Text);
  }

  [Fact]
  public void FenceLine_IndentUpToThreeSpaces_OpeningFence()
  {
    StreamedTextNormalizer n = new();
    n.Append("   ```js");
    n.Append("\nlet x = 1");
    Assert.Equal("   ```js\nlet x = 1", n.Text);
  }

  [Fact]
  public void FenceLine_FourSpaceIndent_IsNotAFence()
  {
    StreamedTextNormalizer n = new();
    n.Append("text\n    ```py");
    n.Append("\nvalue");
    // A four-space-indented fence is prose: no fence protection applies — the wrap
    // joins with a space (the existing clause-wrap rule) and the indent stays as-is.
    Assert.Equal("text     ```pyvalue", n.Text);
  }

  [Fact]
  public void FenceLine_Tildes_OpeningFence()
  {
    StreamedTextNormalizer n = new();
    n.Append("~~~ruby");
    n.Append("\nputs 1");
    Assert.Equal("~~~ruby\nputs 1", n.Text);
  }

  [Fact]
  public void FenceLine_ClosingFence_StartsOwnLine()
  {
    StreamedTextNormalizer n = new();
    n.Append("```js");
    n.Append("\nlet x = 1\n```");
    n.Append("\nafter the fence");
    Assert.Equal("```js\nlet x = 1\n```\nafter the fence", n.Text);
  }

  [Fact]
  public void FenceLine_BlankLineRunInsideFence_PreservedNotCollapsed()
  {
    StreamedTextNormalizer n = new();
    n.Append("```");
    n.Append("\na\n\n\n\nb");
    n.Append("\n```");
    Assert.Equal("```\na\n\n\n\nb\n```", n.Text);
  }

  [Fact]
  public void FenceLine_TextAfterOpeningFence_IsCodeContent()
  {
    StreamedTextNormalizer n = new();
    n.Append("```csharp code here");
    n.Append("\nbody");
    Assert.Equal("```csharp code here\nbody", n.Text);
  }

  [Fact]
  public void FenceLine_NormalRulesResume_AfterClosingFence()
  {
    StreamedTextNormalizer n = new();
    n.Append("```\ncode\n```");
    n.Append("\nhowever, next");
    Assert.Equal("```\ncode\n```\nhowever, next", n.Text);
  }

  [Fact]
  public void EndOfLine_FenceOpener_AfterProse_StartsOwnLine()
  {
    // The model ends a prose line with a fence opener; markdown needs the opener
    // on its own line or the fence never opens and the code renders as prose.
    StreamedTextNormalizer n = new();
    n.Append("some text ```csharp");
    n.Append("\ncode line here");
    Assert.Equal("some text\n```csharp\ncode line here", n.Text);
  }

  [Fact]
  public void EndOfLine_FenceOpener_FirstCodeLine_NotDestroyed()
  {
    // Regression for the glue bug: the opener glued to the following code line
    // produced "```csharpcode line here" - the fence AND the code were destroyed.
    StreamedTextNormalizer n = new();
    n.Append("some text ```csharp");
    n.Append("\npublic class Foo");
    Assert.Equal("some text\n```csharp\npublic class Foo", n.Text);
  }

  [Fact]
  public void EndOfLine_TildeFenceOpener_AfterProse_StartsOwnLine()
  {
    StreamedTextNormalizer n = new();
    n.Append("note ~~~ruby");
    n.Append("\nputs 1");
    Assert.Equal("note\n~~~ruby\nputs 1", n.Text);
  }

  [Fact]
  public void EndOfLine_NumberedListMarker_AfterProse_StartsOwnLine()
  {
    StreamedTextNormalizer n = new();
    n.Append("some text 1. first item");
    n.Append("\n2. second item");
    Assert.Equal("some text\n1. first item\n2. second item", n.Text);
  }

  [Fact]
  public void EndOfLine_NumberedMarker_ViaPendingBreak_StartsOwnLine()
  {
    // "steps:" then a wrap then "1. one": today the digit joins with a space
    // ("steps: 1. one"); the marker rule must force the break instead.
    StreamedTextNormalizer n = new();
    n.Append("steps:");
    n.Append("\n1. one");
    Assert.Equal("steps:\n1. one", n.Text);
  }

  [Fact]
  public void EndOfLine_DecimalNumber_IsNotAListMarker()
  {
    // digit+dot+digit is a decimal, never a list marker: the closing-punctuation
    // rule attaches the ".14" directly.
    StreamedTextNormalizer n = new();
    n.Append("pi is 3");
    n.Append("\n.14 and more");
    Assert.Equal("pi is 3.14 and more", n.Text);
  }

  [Fact]
  public void EndOfLine_NumberDotDigit_AfterProse_JoinsNotBreaks()
  {
    StreamedTextNormalizer n = new();
    n.Append("section");
    n.Append("\n1.2 details");
    Assert.Equal("section 1.2 details", n.Text);
  }

  [Fact]
  public void EndOfLine_StarBullet_ViaPendingBreak_StartsOwnLine()
  {
    // A pending break resolving against "* first" keeps the break (existing rule);
    // this pins that the new marker forcing does not regress the star marker.
    StreamedTextNormalizer n = new();
    n.Append("options");
    n.Append("\n* first");
    Assert.Equal("options\n* first", n.Text);
  }

  [Fact]
  public void EndOfLine_InlineBacktick_SinglePair_IsNotAFence()
  {
    // A single inline-code backtick pair mid-prose must not trigger the fence
    // forcing: only a marker RUN of three or more opens a fence.
    StreamedTextNormalizer n = new();
    n.Append("use the `flag` value");
    n.Append("\nhere");
    Assert.Equal("use the `flag` valuehere", n.Text);
  }

  [Fact]
  public void EndOfLine_FenceOpener_ParagraphBreakBefore_StillCollapses()
  {
    // Two breaks after a glued fence opener: the opener is repaired onto its own
    // line and opens the fence, so the following blank line is code-block content
    // (CommonMark: everything after the opener line is code).
    StreamedTextNormalizer n = new();
    n.Append("para one ```js");
    n.Append("\n\nlet x = 1");
    Assert.Equal("para one\n```js\n\nlet x = 1", n.Text);
  }
}
