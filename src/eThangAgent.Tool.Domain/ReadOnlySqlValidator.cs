using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Pure lexical gate for <c>db_query</c> input: the statement must be a single
///     read-only query. This layer exists to give the model a precise, immediate error;
///     the enforcement backstop is the storage ACL's read-only connection, which rejects
///     every write at the engine level — including writable CTE forms (WITH … INSERT)
///     that this validator deliberately does not try to parse.</summary>
public static class ReadOnlySqlValidator
{
  /// <summary>Returns a <see cref="ToolErrorCodes.InvalidSql"/> error, or null when the
  ///     statement is one query beginning with SELECT or WITH. Semicolons inside string
  ///     literals, quoted identifiers, and comments are never statement separators; a
  ///     single trailing separator is allowed. ATTACH/DETACH are rejected anywhere —
  ///     they are the one construct that could reach a second database file.</summary>
  public static DomainError? Validate(string sql)
  {
    if (string.IsNullOrWhiteSpace(sql))
    {
      return Err("The statement is empty — provide a SELECT or WITH query.");
    }

    string first = NextWord(sql, SkipTrivia(sql, 0), out int _);
    DomainError? head = IsReadOnlyHead(first) ? null : HeadError(first);
    return head ?? ScanAll(sql);
  }

  private static bool IsReadOnlyHead(string first) =>
      first.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
      || first.Equals("WITH", StringComparison.OrdinalIgnoreCase);

  private static DomainError HeadError(string first) => Err(
      $"The statement must begin with SELECT or WITH (got '{first}'). " +
      "db_query only runs read-only queries; writes and pragmas are not allowed.");

  /// <summary>Token loop: walks the statement consuming one token or region at a
  ///     time and returns the first hard error; a trailing-separator end is clean.</summary>
  private static DomainError? ScanAll(string sql)
  {
    bool sawAttach = false;
    int p = 0;
    while (p < sql.Length)
    {
      (DomainError? error, bool done, bool attach) = ScanNext(sql, ref p);
      sawAttach |= attach;
      if (error is not null)
      {
        return error;
      }

      if (done)
      {
        break;
      }
    }

    return sawAttach ? AttachError() : null;
  }

  /// <summary>Consumes one token or region starting at <paramref name="p"/>. Comments
  ///     and quoted regions are consumed wholesale by their handlers, so a ';' seen
  ///     here is always in normal text — a real statement separator. The tuple's
  ///     <c>done</c> element reports a clean end at a trailing separator.</summary>
  private static (DomainError? Error, bool Done, bool Attach) ScanNext(string sql, ref int p)
  {
    char c = sql[p];
    if (IsQuote(c))
    {
      return ScanQuoted(sql, ref p, c);
    }

    if (c == '[')
    {
      return ScanBracketed(sql, ref p);
    }

    (DomainError? Error, bool Done, bool Attach) result = c == ';'
      ? ScanSeparator(sql, ref p)
      : ScanWordOrPunctuation(sql, ref p);
    return result;
  }

  private static bool IsQuote(char c) => c is '\'' or '"' or '`';

  private static (DomainError? Error, bool Done, bool Attach) ScanQuoted(string sql, ref int p, char quote)
  {
    bool closed = SkipQuoted(sql, ref p, quote);
    DomainError? error = quote switch
    {
      '\'' => closed ? null : Err("Unterminated string literal — close the quote."),
      '"' => closed ? null : Err("Unterminated quoted identifier — close the double quote."),
      _ => closed ? null : Err("Unterminated quoted identifier — close the backtick."),
    };
    return (error, false, false);
  }

  private static (DomainError? Error, bool Done, bool Attach) ScanSeparator(string sql, ref int p)
  {
    int after = SkipTrivia(sql, p + 1);
    if (after < sql.Length)
    {
      return (Err("Multiple SQL statements are not allowed — run one query per call."), false, false);
    }

    p = after;
    return (null, true, false);
  }

  private static (DomainError? Error, bool Done, bool Attach) ScanWordOrPunctuation(string sql, ref int p)
  {
    if (IsWordChar(sql[p]))
    {
      return ScanWord(sql, ref p);
    }

    // Whitespace and comments (which may legally contain ';') are skipped as a
    // unit; any other punctuation advances exactly one character.
    int next = SkipTrivia(sql, p);
    p = next > p ? next : p + 1;
    return (null, false, false);
  }

  private static (DomainError? Error, bool Done, bool Attach) ScanWord(string sql, ref int p)
  {
    string word = NextWord(sql, p, out int end);
    p = end;
    bool attach = word.Equals("ATTACH", StringComparison.OrdinalIgnoreCase)
        || word.Equals("DETACH", StringComparison.OrdinalIgnoreCase);
    return (null, false, attach);
  }

  private static (DomainError? Error, bool Done, bool Attach) ScanBracketed(string sql, ref int p)
  {
    int close = sql.IndexOf(']', p + 1);
    if (close < 0)
    {
      return (Err("Unterminated bracketed identifier — close the bracket."), false, false);
    }

    p = close + 1;
    return (null, false, false);
  }

  private static DomainError AttachError() => Err(
      "ATTACH/DETACH is not allowed — db_query only reads the agent's own database.");

  private static DomainError Err(string message) => new(ToolErrorCodes.InvalidSql, message);

  /// <summary>Advances past whitespace, line comments, and block comments. An unterminated
  ///     block comment simply runs to the end of the input.</summary>
  private static int SkipTrivia(string sql, int p)
  {
    while (p < sql.Length)
    {
      int moved = SkipTriviaRun(sql, p);
      if (moved == p)
      {
        break;
      }

      p = moved;
    }

    return p;
  }

  /// <summary>Consumes one trivia element (whitespace run, line comment, or block
  ///     comment) starting at <paramref name="p"/>; returns <paramref name="p"/>
  ///     unchanged when the character there is not trivia.</summary>
  private static int SkipTriviaRun(string sql, int p)
  {
    char c = sql[p];
    if (char.IsWhiteSpace(c))
    {
      while (p < sql.Length && char.IsWhiteSpace(sql[p]))
      {
        p++;
      }

      return p;
    }

    if (IsLineComment(c, p, sql))
    {
      return LineCommentEnd(sql, p);
    }

    int moved = IsBlockComment(c, p, sql) ? BlockCommentEnd(sql, p) : p;
    return moved;
  }

  private static bool IsLineComment(char c, int p, string sql) =>
      c == '-' && p + 1 < sql.Length && sql[p + 1] == '-';

  private static bool IsBlockComment(char c, int p, string sql) =>
      c == '/' && p + 1 < sql.Length && sql[p + 1] == '*';

  private static int LineCommentEnd(string sql, int p)
  {
    int eol = sql.IndexOf('\n', p + 2);
    return eol < 0 ? sql.Length : eol + 1;
  }

  private static int BlockCommentEnd(string sql, int p)
  {
    int end = sql.IndexOf("*/", p + 2, StringComparison.Ordinal);
    return end < 0 ? sql.Length : end + 2;
  }

  /// <summary>Advances past a region opened by <paramref name="quote"/>, where a doubled
  ///     quote is an escape. Returns false when the input ends before the close.</summary>
  private static bool SkipQuoted(string sql, ref int p, char quote)
  {
    p++;
    while (p < sql.Length)
    {
      if (sql[p] != quote)
      {
        p++;
      }
      else if (p + 1 < sql.Length && sql[p + 1] == quote)
      {
        p += 2;
      }
      else
      {
        p++;
        return true;
      }
    }

    return false;
  }

  private static string NextWord(string sql, int start, out int end)
  {
    int p = start;
    while (p < sql.Length && IsWordChar(sql[p]))
    {
      p++;
    }

    end = p;
    return sql[start..p];
  }

  private static bool IsWordChar(char c) =>
      char.IsLetterOrDigit(c) || c is '_' or '$';
}
