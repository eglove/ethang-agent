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
    ///     Validates the parts and renders the commit message. Each violation is its
    ///     own error, checked in rule order: style → style-specific parameter rules →
    ///     description → scope. Body never fails validation.
    /// </summary>
    public static Result<CommitMessage> Create(
        string style, string? type, string? scope, string? emojiKey,
        string description, string? body)
    {
        // Rule 1 — style must be exactly one of the three names (ordinal).
        var parsedStyle = style switch
        {
            nameof(CommitStyle.Conventional) => CommitStyle.Conventional,
            nameof(CommitStyle.Gitmoji) => CommitStyle.Gitmoji,
            nameof(CommitStyle.None) => CommitStyle.None,
            _ => (CommitStyle?)null,
        };
        if (parsedStyle is null)
        {
            return Failure("InvalidStyle",
                $"'style' must be exactly one of: {nameof(CommitStyle.Conventional)}, " +
                $"{nameof(CommitStyle.Gitmoji)}, {nameof(CommitStyle.None)} (case-sensitive) " +
                $"— got '{style}'.");
        }

        Gitmoji? gitmoji = null;
        switch (parsedStyle)
        {
            case CommitStyle.Conventional:
                // Rule 2a — type is required.
                if (string.IsNullOrEmpty(type))
                {
                    return Failure("TypeRequired",
                        $"'type' is required for the Conventional style. " +
                        $"Use one of: {string.Join(", ", KnownTypes)}.");
                }

                // Rule 2b — type must be from the fixed set (ordinal match).
                if (!KnownTypes.Contains(type, StringComparer.Ordinal))
                {
                    return Failure("UnknownType",
                        $"'type' '{type}' is not a known Conventional Commit type. " +
                        $"Use one of: {string.Join(", ", KnownTypes)}.");
                }

                // Rule 2c — emojiKey carries no meaning next to an explicit type.
                if (!string.IsNullOrEmpty(emojiKey))
                {
                    return Failure("ParameterNotAllowed",
                        "'emojiKey' is not allowed with the Conventional style — " +
                        "the type already carries the intent. Omit 'emojiKey'.");
                }

                break;

            case CommitStyle.Gitmoji:
                // Rule 3a — emojiKey is required.
                if (string.IsNullOrEmpty(emojiKey))
                {
                    return Failure("EmojiKeyRequired",
                        "'emojiKey' is required for the Gitmoji style — pass an exact " +
                        "':name:' key from the gitmoji catalog.");
                }

                // Rule 3b — unknown keys surface the catalog's error verbatim.
                var lookup = GitmojiCatalog.Lookup(emojiKey);
                if (!lookup.IsSuccess)
                {
                    return Result<CommitMessage>.Failure(lookup.Error!);
                }

                gitmoji = lookup.Value;

                // Rule 3c — type/scope carry no meaning next to an emoji.
                var forbidden = PresentAmong(type, scope, emojiKey: null);
                if (forbidden.Length > 0)
                {
                    return Failure("ParameterNotAllowed",
                        $"'{string.Join("', '", forbidden)}' not allowed with the Gitmoji " +
                        "style — the emoji already carries the type. Omit them.");
                }

                break;

            case CommitStyle.None:
                // Rule 4 — none of type/scope/emojiKey may be present; name offenders.
                var present = PresentAmong(type, scope, emojiKey);
                if (present.Length > 0)
                {
                    return Failure("ParameterNotAllowed",
                        $"'{string.Join("', '", present)}' not allowed with the None style — " +
                        "the description stands alone. Omit them.");
                }

                break;
        }

        // Rule 5 — description: required, single-line, at most 72 chars trimmed;
        // stored trimmed.
        if (string.IsNullOrWhiteSpace(description))
        {
            // Whitespace-only counts as missing: the stored value is the trimmed
            // content, and empty content is no content.
            return Failure("MissingDescription",
                "'description' is required and must contain non-whitespace content.");
        }

        if (description.IndexOfAny(['\n', '\r']) >= 0)
        {
            return Failure("MultilineDescription",
                "'description' must be a single line — no newline characters. " +
                "Put additional content in 'body'.");
        }

        var trimmedDescription = description.Trim();
        if (trimmedDescription.Length > MaxDescriptionLength)
        {
            return Failure("DescriptionTooLong",
                $"'description' must be at most {MaxDescriptionLength} characters after " +
                $"trimming — got {trimmedDescription.Length}.");
        }

        // Rule 6 — scope (Conventional only): ^[a-z0-9-]+$; stored as given.
        if (parsedStyle == CommitStyle.Conventional &&
            !string.IsNullOrEmpty(scope) && !IsValidScope(scope))
        {
            return Failure("InvalidScope",
                $"'scope' '{scope}' is invalid — it must match ^[a-z0-9-]+$ " +
                "(lowercase letters, digits, hyphens).");
        }

        // Rendering — deterministic subject + optional blank-line/body, one trailing \n.
        var subject = parsedStyle switch
        {
            CommitStyle.Conventional => string.IsNullOrEmpty(scope)
                ? $"{type}: {trimmedDescription}"
                : $"{type}({scope}): {trimmedDescription}",
            CommitStyle.Gitmoji => $"{gitmoji!.Emoji} {trimmedDescription}",
            _ => trimmedDescription,
        };

        var rendered = string.IsNullOrEmpty(body)
            ? $"{subject}\n"
            : $"{subject}\n\n{body}\n";

        return Result<CommitMessage>.Success(new CommitMessage(rendered, subject));
    }

    /// <summary>Returns the names of whichever parameters carry a value.</summary>
    private static string[] PresentAmong(string? type, string? scope, string? emojiKey)
    {
        var present = new List<string>(3);
        if (!string.IsNullOrEmpty(type)) present.Add(nameof(type));
        if (!string.IsNullOrEmpty(scope)) present.Add(nameof(scope));
        if (!string.IsNullOrEmpty(emojiKey)) present.Add(nameof(emojiKey));
        return [.. present];
    }

    /// <summary>Equivalent to <c>^[a-z0-9-]+$</c> without a regex dependency.</summary>
    private static bool IsValidScope(string scope) =>
        scope.All(c => c is '-' or (>= 'a' and <= 'z') or (>= '0' and <= '9'));

    private static Result<CommitMessage> Failure(string code, string message) =>
        Result<CommitMessage>.Failure(new Error(code, message));
}
