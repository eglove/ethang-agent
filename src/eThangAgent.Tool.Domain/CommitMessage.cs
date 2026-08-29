using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The supported commit message styles.</summary>
public enum CommitStyle
{
  Conventional,
  Gitmoji,
  None,
}

/// <summary>
///     A fully validated, deterministically rendered commit message.
///     <see cref="Subject"/> is the first line; <see cref="Rendered"/> is the full
///     message — subject, then an optional blank line and body — ending in exactly
///     one trailing newline. Assembly is pure: no clock, no I/O, no git.
/// </summary>
/// <param name="Rendered">The complete commit message, ending in a single <c>\n</c>.</param>
/// <param name="Subject">The first line of the message, without trailing newline.</param>
public sealed record CommitMessage(string Rendered, string Subject)
{
  /// <summary>Maximum description length, measured after trimming.</summary>
  private const int MaxDescriptionLength = 72;

  private static readonly string[] KnownTypes =
  [
      "feat", "fix", "docs", "style", "refactor",
        "perf", "test", "build", "ci", "chore", "revert",
    ];

  /// <summary>
  ///     Validates the parts and renders the commit message. The style arrives
  ///     already resolved (a host setting; see <c>CommitStylePreference</c> for how
  ///     a stored value becomes the enum). Each violation is its own error,
  ///     checked in rule order: style-specific parameter rules → description →
  ///     scope. Body never fails validation.
  /// </summary>
  public static Result<CommitMessage> Create(
      CommitStyle style, string? type, string? scope, string? emojiKey,
      string description, string? body)
  {
    // Rules 2-4 — per-style parameter rules (Gitmoji resolves its emoji here).
    DomainError? styleRules = ValidateStyleRules(style, type, scope, emojiKey, out Gitmoji? gitmoji);
    if (styleRules is not null)
    {
      return Result.Failure<CommitMessage>(styleRules);
    }

    // Rule 5 — description: required, single-line, at most 72 chars trimmed,
    // stored trimmed.
    Result<string> parsedDescription = ValidateDescription(description);
    if (!parsedDescription.IsSuccess)
    {
      return Result.Failure<CommitMessage>(parsedDescription.Error);
    }

    // Rule 6 — scope (Conventional only): ^[a-z0-9-]+$; stored as given.
    DomainError? scopeRule = ValidateScopeRule(style, scope);
    if (scopeRule is not null)
    {
      return Result.Failure<CommitMessage>(scopeRule);
    }

    // Rendering — deterministic subject + optional blank-line/body, one trailing \n.
    // The style argument is already the parsed enum value.
    string subject = RenderSubject(style, type, scope, gitmoji, parsedDescription.Value);

    // Trailing newline(s) on the body are trimmed at render time so the message
    // ends with exactly one newline regardless of how the caller formatted the body —
    // a body reduced to nothing by that trim renders as absent. (Named leniency:
    // apart from this trim, bodies are kept verbatim.) Validation rules are
    // unchanged — body never fails validation.
    string renderedBody = (body ?? string.Empty).TrimEnd('\n');
    string rendered = string.IsNullOrEmpty(renderedBody)
        ? $"{subject}\n"
        : $"{subject}\n\n{renderedBody}\n";

    CommitMessage message = new(rendered, subject);
    return Result.Success(message);
  }

  /// <summary>Rules 2-4 — the style-specific parameter rules. Returns the violation,
  ///     or null; <paramref name="gitmoji"/> is resolved for the Gitmoji style.</summary>
  private static DomainError? ValidateStyleRules(CommitStyle style, string? type, string? scope,
      string? emojiKey, out Gitmoji? gitmoji)
  {
    gitmoji = null;
    if (style == CommitStyle.Conventional)
    {
      return ValidateConventional(type, emojiKey);
    }

    if (style == CommitStyle.Gitmoji)
    {
      return ValidateGitmoji(emojiKey, type, scope, out gitmoji);
    }

    return ValidateNoneParams(type, scope, emojiKey); // CommitStyle.None
  }

  /// <summary>Rule 2 — Conventional: type required and known; emojiKey carries no
  ///     meaning next to an explicit type.</summary>
  private static DomainError? ValidateConventional(string? type, string? emojiKey)
  {
    // Rule 2a — type is required.
    if (string.IsNullOrEmpty(type))
    {
      return new DomainError("TypeRequired",
          $"'type' is required for the Conventional style. " +
          $"Use one of: {string.Join(", ", KnownTypes)}.");
    }

    // Rule 2b — type must be from the fixed set (ordinal match).
    if (!KnownTypes.Contains(type, StringComparer.Ordinal))
    {
      return new DomainError("UnknownType",
          $"'type' '{type}' is not a known Conventional Commit type. " +
          $"Use one of: {string.Join(", ", KnownTypes)}.");
    }

    // Rule 2c — emojiKey carries no meaning next to an explicit type.
    DomainError? extra = string.IsNullOrEmpty(emojiKey)
      ? null
      : new DomainError("ParameterNotAllowed",
          "'emojiKey' is not allowed with the Conventional style — " +
          "the type already carries the intent. Omit 'emojiKey'.");
    return extra;
  }

  /// <summary>Rule 3 — Gitmoji: emojiKey required and resolvable; type/scope carry no
  ///     meaning next to an emoji.</summary>
  private static DomainError? ValidateGitmoji(string? emojiKey, string? type, string? scope, out Gitmoji? gitmoji)
  {
    gitmoji = null;

    // Rule 3a — emojiKey is required.
    if (string.IsNullOrEmpty(emojiKey))
    {
      return new DomainError("EmojiKeyRequired",
          "'emojiKey' is required for the Gitmoji style — pass an exact " +
          "':name:' key from the gitmoji catalog.");
    }

    // Rule 3b — unknown keys surface the catalog's error verbatim.
    Result<Gitmoji> lookup = GitmojiCatalog.Lookup(emojiKey);
    if (!lookup.IsSuccess)
    {
      return lookup.Error;
    }

    // Rule 3c — type/scope carry no meaning next to an emoji.
    string[] forbidden = PresentAmong(type, scope, emojiKey: null);
    if (forbidden.Length > 0)
    {
      return new DomainError("ParameterNotAllowed",
          $"'{string.Join("', '", forbidden)}' not allowed with the Gitmoji " +
          "style — the emoji already carries the type. Omit them.");
    }

    gitmoji = lookup.Value;
    return null;
  }

  /// <summary>Rule 4 — None: none of type/scope/emojiKey may be present; name offenders.</summary>
  private static DomainError? ValidateNoneParams(string? type, string? scope, string? emojiKey)
  {
    string[] present = PresentAmong(type, scope, emojiKey);
    DomainError? forbidden = present.Length > 0
      ? new DomainError("ParameterNotAllowed",
          $"'{string.Join("', '", present)}' not allowed with the None style — " +
          "the description stands alone. Omit them.")
      : null;
    return forbidden;
  }

  /// <summary>Rule 5 — description: required, single-line, at most 72 chars trimmed.
  ///     Whitespace-only counts as missing: the stored value is the trimmed content,
  ///     and empty content is no content. Returns the trimmed text on success.</summary>
  private static Result<string> ValidateDescription(string description)
  {
    if (string.IsNullOrWhiteSpace(description))
    {
      return Result.Failure<string>(new DomainError("MissingDescription",
          "'description' is required and must contain non-whitespace content."));
    }

    if (description.IndexOfAny(['\n', '\r']) >= 0)
    {
      return Result.Failure<string>(new DomainError("MultilineDescription",
          "'description' must be a single line — no newline characters. " +
          "Put additional content in 'body'."));
    }

    string trimmed = description.Trim();
    Result<string> result = trimmed.Length <= MaxDescriptionLength
      ? Result.Success(trimmed)
      : Result.Failure<string>(new DomainError("DescriptionTooLong",
          $"'description' must be at most {MaxDescriptionLength} characters after " +
          $"trimming — got {trimmed.Length}."));
    return result;
  }

  /// <summary>Rule 6 — scope (Conventional only): ^[a-z0-9-]+$; stored as given.</summary>
  private static DomainError? ValidateScopeRule(CommitStyle style, string? scope)
  {
    DomainError? invalid = style == CommitStyle.Conventional &&
        !string.IsNullOrEmpty(scope) && !IsValidScope(scope)
      ? new DomainError("InvalidScope",
          $"'scope' '{scope}' is invalid — it must match ^[a-z0-9-]+$ " +
          "(lowercase letters, digits, hyphens).")
      : null;
    return invalid;
  }

  /// <summary>Returns the names of whichever parameters carry a value.</summary>
  private static string[] PresentAmong(string? type, string? scope, string? emojiKey)
  {
    List<string> present = new(3);
    if (!string.IsNullOrEmpty(type))
    {
      present.Add(nameof(type));
    }

    if (!string.IsNullOrEmpty(scope))
    {
      present.Add(nameof(scope));
    }

    if (!string.IsNullOrEmpty(emojiKey))
    {
      present.Add(nameof(emojiKey));
    }

    return [.. present];
  }

  /// <summary>Equivalent to <c>^[a-z0-9-]+$</c> without a regex dependency.</summary>
  private static bool IsValidScope(string scope) =>
      scope.All(c => c is '-' or (>= 'a' and <= 'z') or (>= '0' and <= '9'));

  /// <summary>Guard-style early returns: the switch shapes (expression or statement) are
  /// each rejected by one of CS8524 / IDE0072 / IDE0066 / IDE0010 / S2583, and if/else
  /// assignment is rejected by IDE0045.</summary>
  private static string RenderSubject(CommitStyle style, string? type, string? scope,
      Gitmoji? gitmoji, string description)
  {
    if (style == CommitStyle.Gitmoji)
    {
      return $"{gitmoji!.Emoji} {description}";
    }

    if (style == CommitStyle.Conventional)
    {
      return string.IsNullOrEmpty(scope)
          ? $"{type}: {description}"
          : $"{type}({scope}): {description}";
    }

    return description; // CommitStyle.None
  }
}
