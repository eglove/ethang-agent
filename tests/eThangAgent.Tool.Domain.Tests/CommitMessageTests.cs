using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class CommitMessageTests
{
  private static DomainError ErrorOf(Result<CommitMessage> result) =>
      result.Error ?? throw new InvalidOperationException("expected failure carried no error");

  // ── Rule 1: style must be exactly Conventional | Gitmoji | None (ordinal) ──

  [Fact]
  public void UnknownStyle_InvalidStyle_ListsTheThreeStyles()
  {
    Result<CommitMessage> r = CommitMessage.Create("Semantic", type: "feat", scope: null, emojiKey: null,
            description: "add write tool", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidStyle", ErrorOf(r).Code);
    Assert.Multiple(
        () => Assert.Contains("Conventional", ErrorOf(r).Message, StringComparison.Ordinal),
        () => Assert.Contains("Gitmoji", ErrorOf(r).Message, StringComparison.Ordinal),
        () => Assert.Contains("None", ErrorOf(r).Message, StringComparison.Ordinal));
  }

  [Theory]
  [InlineData("conventional")]
  [InlineData("GITMOJI")]
  [InlineData("none")]
  [InlineData(null)]
  public void Style_MustMatchExactlyOrdinal(string? style)
  {
    Result<CommitMessage> r = CommitMessage.Create(style!, type: null, scope: null, emojiKey: null,
            description: "add write tool", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidStyle", ErrorOf(r).Code);
  }

  // ── Rule 2: Conventional ──

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  public void Conventional_TypeMissing_TypeRequired(string? type)
  {
    Result<CommitMessage> r = CommitMessage.Create("Conventional", type: type, scope: null, emojiKey: null,
            description: "add write tool", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("TypeRequired", ErrorOf(r).Code);
  }

  [Theory]
  [InlineData("feature")]
  [InlineData("Feat")]
  [InlineData("FEAT")]
  [InlineData("feat ")]
  public void Conventional_TypeOutsideFixedSet_UnknownType_ListsTheSet(string type)
  {
    Result<CommitMessage> r = CommitMessage.Create("Conventional", type: type, scope: null, emojiKey: null,
            description: "add write tool", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("UnknownType", ErrorOf(r).Code);
    foreach (string known in new[] { "feat", "fix", "docs", "style", "refactor",
                     "perf", "test", "build", "ci", "chore", "revert" })
    {
      Assert.Contains(known, ErrorOf(r).Message, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void Conventional_EmojiKeyPresent_ParameterNotAllowed()
  {
    Result<CommitMessage> r = CommitMessage.Create("Conventional", type: "feat", scope: null,
            emojiKey: ":tada:", description: "add write tool", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("ParameterNotAllowed", ErrorOf(r).Code);
    Assert.Contains("emojiKey", ErrorOf(r).Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Conventional_WithScope_ExactSubjectAndRendered()
  {
    Result<CommitMessage> r = CommitMessage.Create("Conventional", type: "feat", scope: "tools",
            emojiKey: null, description: "add write tool", body: null);
    Assert.True(r.IsSuccess);
    Assert.Equal("feat(tools): add write tool", r.Value.Subject);
    Assert.Equal("feat(tools): add write tool\n", r.Value.Rendered);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  public void Conventional_ScopeAbsent_RendersWithoutParentheses(string? scope)
  {
    Result<CommitMessage> r = CommitMessage.Create("Conventional", type: "feat", scope: scope,
            emojiKey: null, description: "add write tool", body: null);
    Assert.True(r.IsSuccess);
    Assert.Equal("feat: add write tool", r.Value.Subject);
    Assert.Equal("feat: add write tool\n", r.Value.Rendered);
  }

  [Theory]
  [InlineData("Tools")]
  [InlineData("a_b")]
  [InlineData("two words")]
  [InlineData("tool.name")]
  public void Conventional_ScopeViolatingPattern_InvalidScope(string scope)
  {
    Result<CommitMessage> r = CommitMessage.Create("Conventional", type: "chore", scope: scope,
            emojiKey: null, description: "add write tool", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidScope", ErrorOf(r).Code);
    Assert.Contains("a-z0-9-", ErrorOf(r).Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("tools")]
  [InlineData("a-1-b")]
  [InlineData("0")]
  [InlineData("-x-")]
  public void Conventional_ValidScopeForms_StoredAsGiven(string scope)
  {
    Result<CommitMessage> r = CommitMessage.Create("Conventional", type: "chore", scope: scope,
            emojiKey: null, description: "tidy up", body: null);
    Assert.True(r.IsSuccess);
    Assert.Equal($"chore({scope}): tidy up", r.Value.Subject);
  }

  // ── Rule 3: Gitmoji ──

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  public void Gitmoji_EmojiKeyMissing_EmojiKeyRequired(string? emojiKey)
  {
    Result<CommitMessage> r = CommitMessage.Create("Gitmoji", type: null, scope: null, emojiKey: emojiKey,
            description: "add write tool", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("EmojiKeyRequired", ErrorOf(r).Code);
  }

  [Fact]
  public void Gitmoji_UnknownKey_CatalogErrorSurfacesVerbatim()
  {
    const string key = ":definitely_not_a_gitmoji:";
    Result<Gitmoji> expected = GitmojiCatalog.Lookup(key);
    Assert.False(expected.IsSuccess); // sanity: the catalog must reject this key

    Result<CommitMessage> r = CommitMessage.Create("Gitmoji", type: null, scope: null, emojiKey: key,
            description: "add write tool", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal(expected.Error.Code, ErrorOf(r).Code);
    Assert.Equal(expected.Error.Message, ErrorOf(r).Message);
  }

  [Fact]
  public void Gitmoji_HappyPath_RendersEmojiAndDescription()
  {
    Result<CommitMessage> r = CommitMessage.Create("Gitmoji", type: null, scope: null, emojiKey: ":sparkles:",
            description: "Introduce new features", body: null);
    Assert.True(r.IsSuccess);
    Assert.Equal("\u2728 Introduce new features", r.Value.Subject);
    Assert.Equal("\u2728 Introduce new features\n", r.Value.Rendered);
  }

  // ── Rule 3c: type/scope forbidden with Gitmoji ──

  [Fact]
  public void Gitmoji_TypePresent_ParameterNotAllowed_NamesIt()
  {
    Result<CommitMessage> r = CommitMessage.Create("Gitmoji", type: "feat", scope: null, emojiKey: ":sparkles:",
            description: "Introduce new features", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("ParameterNotAllowed", ErrorOf(r).Code);
    Assert.Contains("type", ErrorOf(r).Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Gitmoji_ScopePresent_ParameterNotAllowed_NamesIt()
  {
    Result<CommitMessage> r = CommitMessage.Create("Gitmoji", type: null, scope: "tools", emojiKey: ":sparkles:",
            description: "Introduce new features", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("ParameterNotAllowed", ErrorOf(r).Code);
    Assert.Contains("scope", ErrorOf(r).Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Gitmoji_BothTypeAndScope_MessageNamesBoth()
  {
    Result<CommitMessage> r = CommitMessage.Create("Gitmoji", type: "feat", scope: "tools", emojiKey: ":sparkles:",
            description: "Introduce new features", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("ParameterNotAllowed", ErrorOf(r).Code);
    Assert.Multiple(
        () => Assert.Contains("type", ErrorOf(r).Message, StringComparison.Ordinal),
        () => Assert.Contains("scope", ErrorOf(r).Message, StringComparison.Ordinal));
  }

  // ── Rule 4: None forbids type/scope/emojiKey, message names which ──

  [Fact]
  public void None_TypePresent_ParameterNotAllowed_NamesIt()
  {
    Result<CommitMessage> r = CommitMessage.Create("None", type: "feat", scope: null, emojiKey: null,
            description: "plain note", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("ParameterNotAllowed", ErrorOf(r).Code);
    Assert.Contains("type", ErrorOf(r).Message, StringComparison.Ordinal);
  }

  [Fact]
  public void None_ScopePresent_ParameterNotAllowed_NamesIt()
  {
    Result<CommitMessage> r = CommitMessage.Create("None", type: null, scope: "tools", emojiKey: null,
            description: "plain note", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("ParameterNotAllowed", ErrorOf(r).Code);
    Assert.Contains("scope", ErrorOf(r).Message, StringComparison.Ordinal);
  }

  [Fact]
  public void None_EmojiKeyPresent_ParameterNotAllowed_NamesIt()
  {
    Result<CommitMessage> r = CommitMessage.Create("None", type: null, scope: null, emojiKey: ":tada:",
            description: "plain note", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("ParameterNotAllowed", ErrorOf(r).Code);
    Assert.Contains("emojiKey", ErrorOf(r).Message, StringComparison.Ordinal);
  }

  [Fact]
  public void None_AllThreePresent_MessageNamesAllThree()
  {
    Result<CommitMessage> r = CommitMessage.Create("None", type: "feat", scope: "tools", emojiKey: ":tada:",
            description: "plain note", body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("ParameterNotAllowed", ErrorOf(r).Code);
    Assert.Multiple(
        () => Assert.Contains("type", ErrorOf(r).Message, StringComparison.Ordinal),
        () => Assert.Contains("scope", ErrorOf(r).Message, StringComparison.Ordinal),
        () => Assert.Contains("emojiKey", ErrorOf(r).Message, StringComparison.Ordinal));
  }

  [Fact]
  public void None_HappyPath_RendersDescriptionOnly()
  {
    Result<CommitMessage> r = CommitMessage.Create("None", type: null, scope: null, emojiKey: null,
            description: "plain note", body: null);
    Assert.True(r.IsSuccess);
    Assert.Equal("plain note", r.Value.Subject);
    Assert.Equal("plain note\n", r.Value.Rendered);
  }

  // ── Rule 5: description ──

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  public void DescriptionMissingOrWhitespace_MissingDescription(string? description)
  {
    Result<CommitMessage> r = CommitMessage.Create("None", type: null, scope: null, emojiKey: null,
            description: description!, body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("MissingDescription", ErrorOf(r).Code);
  }

  [Theory]
  [InlineData("two\nlines")]
  [InlineData("carriage\rreturn")]
  [InlineData("crlf\r\nline")]
  public void DescriptionContainingNewline_MultilineDescription(string description)
  {
    Result<CommitMessage> r = CommitMessage.Create("None", type: null, scope: null, emojiKey: null,
            description: description, body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("MultilineDescription", ErrorOf(r).Code);
  }

  [Fact]
  public void DescriptionTooLong_ErrorNamesLimitAndActual()
  {
    const string tooLong = "this description is deliberately padded so that it measures seventy-three";
    Assert.Equal(73, tooLong.Length); // guard the fixture itself

    Result<CommitMessage> r = CommitMessage.Create("None", type: null, scope: null, emojiKey: null,
            description: tooLong, body: null);
    Assert.False(r.IsSuccess);
    Assert.Equal("DescriptionTooLong", ErrorOf(r).Code);
    Assert.Multiple(
        () => Assert.Contains("72", ErrorOf(r).Message, StringComparison.Ordinal),
        () => Assert.Contains("73", ErrorOf(r).Message, StringComparison.Ordinal));
  }

  [Fact]
  public void Description_ExactlyAtLimit_AcceptedUntrimmed()
  {
    const string atLimit = "this description is deliberately padded so that it measures exactly 72!x";
    Assert.Equal(72, atLimit.Length);

    Result<CommitMessage> r = CommitMessage.Create("None", type: null, scope: null, emojiKey: null,
            description: atLimit, body: null);
    Assert.True(r.IsSuccess);
    Assert.Equal(atLimit, r.Value.Subject);
  }

  [Fact]
  public void Description_StoredTrimmed()
  {
    Result<CommitMessage> r = CommitMessage.Create("Conventional", type: "fix", scope: null, emojiKey: null,
            description: "  pad both ends  ", body: null);
    Assert.True(r.IsSuccess);
    Assert.Equal("fix: pad both ends", r.Value.Subject);
    Assert.Equal("fix: pad both ends\n", r.Value.Rendered);
  }

  // ── Rule 7 + rendering: body ──

  [Fact]
  public void Body_AppendedAfterBlankLine_EndsWithSingleTrailingNewline()
  {
    Result<CommitMessage> r = CommitMessage.Create("Conventional", type: "feat", scope: "tools", emojiKey: null,
            description: "add write tool", body: "wrap-up notes");
    Assert.True(r.IsSuccess);
    Assert.Equal("feat(tools): add write tool", r.Value.Subject);
    Assert.Equal("feat(tools): add write tool\n\nwrap-up notes\n", r.Value.Rendered);
  }

  [Fact]
  public void Body_MultiLine_StoredVerbatim()
  {
    Result<CommitMessage> r = CommitMessage.Create("None", type: null, scope: null, emojiKey: null,
            description: "plain note", body: "line one\nline two");
    Assert.True(r.IsSuccess);
    Assert.Equal("plain note\n\nline one\nline two\n", r.Value.Rendered);
  }

  [Theory]
  [InlineData("wrap-up notes")]
  [InlineData("wrap-up notes\n")]
  [InlineData("wrap-up notes\n\n\n")]
  public void Body_TrailingNewlines_TrimmedAtRender_SingleTrailingNewline(string body)
  {
    // Render-time normalization keeps the "single trailing \n" contract even
    // when the caller's body ends in newline(s); validation rules untouched.
    Result<CommitMessage> r = CommitMessage.Create("None", type: null, scope: null, emojiKey: null,
            description: "plain note", body: body);
    Assert.True(r.IsSuccess);
    Assert.Equal("plain note\n\nwrap-up notes\n", r.Value.Rendered);
  }

  [Fact]
  public void Body_AllNewlines_TrimsAway_RendersAsAbsent()
  {
    Result<CommitMessage> r = CommitMessage.Create("None", type: null, scope: null, emojiKey: null,
            description: "plain note", body: "\n\n");
    Assert.True(r.IsSuccess);
    Assert.Equal("plain note\n", r.Value.Rendered);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  public void Body_NullOrEmpty_NoBodySection(string? body)
  {
    Result<CommitMessage> r = CommitMessage.Create("None", type: null, scope: null, emojiKey: null,
            description: "plain note", body: body);
    Assert.True(r.IsSuccess);
    Assert.Equal("plain note\n", r.Value.Rendered);
  }

  // ── Rule order: each numbered rule is checked in brief order ──

  [Fact]
  public void Order_StyleCheckedFirst_BeforeAllOtherRules()
  {
    Result<CommitMessage> r = CommitMessage.Create("nope", type: null, scope: null, emojiKey: null,
            description: "", body: null);
    Assert.Equal("InvalidStyle", ErrorOf(r).Code);
  }

  [Fact]
  public void Order_Conventional_TypeRulesBeforeDescriptionAndScope()
  {
    // Missing type beats unknown scope and missing description.
    Assert.Equal("TypeRequired", ErrorOf(CommitMessage.Create(
        "Conventional", type: null, scope: "BAD", emojiKey: null,
        description: "", body: null)).Code);
    // Unknown type beats missing description.
    Assert.Equal("UnknownType", ErrorOf(CommitMessage.Create(
        "Conventional", type: "bogus", scope: "BAD", emojiKey: null,
        description: "", body: null)).Code);
    // Known type + forbidden emojiKey beats missing description.
    Assert.Equal("ParameterNotAllowed", ErrorOf(CommitMessage.Create(
        "Conventional", type: "feat", scope: null, emojiKey: ":tada:",
        description: "", body: null)).Code);
  }

  [Fact]
  public void Order_DescriptionCheckedBeforeScope()
  {
    Result<CommitMessage> r = CommitMessage.Create("Conventional", type: "feat", scope: "BAD", emojiKey: null,
            description: "", body: null);
    Assert.Equal("MissingDescription", ErrorOf(r).Code);
  }

  [Fact]
  public void Order_Gitmoji_LookupFailureBeforeForbiddenParams()
  {
    Result<CommitMessage> r = CommitMessage.Create("Gitmoji", type: "feat", scope: null,
            emojiKey: ":not_a_key:", description: "x", body: null);
    Assert.Equal("UnknownEmojiKey", ErrorOf(r).Code);
  }
}
