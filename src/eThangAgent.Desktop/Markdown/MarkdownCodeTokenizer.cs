using System.Text;

namespace eThangAgent.Desktop.Markdown;

/// <summary>Token kinds the code tokenizer distinguishes. Deliberately minimal:
///     the chat palette needs categories, not a parse tree.</summary>
internal enum MarkdownCodeTokenKind
{
  Default = 0,
  Comment,
  String,
  Number,
  Keyword,
}

/// <summary>One colored span of code text, contiguous and ordered with its
///     siblings; concatenating all spans reproduces the input exactly.</summary>
internal readonly record struct MarkdownCodeToken(string Text, MarkdownCodeTokenKind Kind);

/// <summary>Hand-rolled, dependency-free tokenizer for fenced code blocks in chat.
///     Tokenizes the WHOLE text first (so comments and strings that span newlines
///     stay one colored span); the renderer splits spans at newlines when it emits
///     runs. Unknown or empty language yields one Default span, so unlisted code
///     renders exactly as before. No error paths: worst case is monochrome.</summary>
internal static class MarkdownCodeTokenizer
{
  private const char Dq = (char)34;   // double quote
  private const char Sq = (char)39;   // apostrophe
  private const char Backtick = (char)96;
  private const char Backslash = (char)92;

  // Character literals in the language table use numeric forms ((char)34 = double quote,
  // (char)39 = apostrophe, (char)92 = backslash) to keep the source escape-free.
  private sealed class LangDef
  {
    public required string[] LineCommentPrefixes { get; init; }
    public required string[] BlockComment { get; init; }
    public required char[] StringDelims { get; init; }
    public required bool HasVerbatimString { get; init; }
    public required string[] Keywords { get; init; }
    public bool UseXmlMode { get; init; }
  }

  private static readonly LangDef CSharp = new()
  {
    LineCommentPrefixes = ["//"],
    BlockComment = ["/*", "*/"],
    StringDelims = [(char)34],
    HasVerbatimString = true,
    Keywords = [
      "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
      "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
      "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object",
      "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
      "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
      "ushort", "using", "var", "virtual", "void", "volatile", "while"],
  };
  private static readonly LangDef JavaScript = new()
  {
    LineCommentPrefixes = ["//"],
    BlockComment = ["/*", "*/"],
    StringDelims = [(char)34, (char)39, (char)96],
    HasVerbatimString = false,
    Keywords = ["break", "case", "catch", "class", "const", "continue", "default", "delete", "do", "else", "export", "extends", "finally",
      "for", "function", "if", "import", "in", "instanceof", "let", "new", "null", "of", "return", "static", "super", "switch", "this", "throw",
      "true", "false", "try", "typeof", "undefined", "var", "while", "yield"],
  };
  private static readonly LangDef Python = new()
  {
    LineCommentPrefixes = ["#"],
    BlockComment = [],
    StringDelims = [(char)34, (char)39],
    HasVerbatimString = false,
    Keywords = ["and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "False",
      "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "None", "nonlocal", "not", "or", "pass", "raise", "return",
      "True", "try", "while", "with", "yield"],
  };
  private static readonly LangDef XmlDef = new()
  {
    LineCommentPrefixes = [],
    BlockComment = ["<!--", "-->"],
    StringDelims = [(char)34, (char)39],
    HasVerbatimString = false,
    Keywords = [],
    UseXmlMode = true,
  };
  private static readonly LangDef Bash = new()
  {
    LineCommentPrefixes = ["#"],
    BlockComment = [],
    StringDelims = [(char)34, (char)39],
    HasVerbatimString = false,
    Keywords = ["if", "then", "else", "elif", "fi", "for", "while", "case", "esac", "function", "do", "done", "in", "select", "until", "time", "return", "exit"],
  };
  private static readonly LangDef JsonDef = new()
  {
    LineCommentPrefixes = [],
    BlockComment = [],
    StringDelims = [(char)34],
    HasVerbatimString = false,
    Keywords = ["true", "false", "null"],
  };
  private static readonly Dictionary<string, LangDef> Languages = new(StringComparer.OrdinalIgnoreCase)
  {
    ["csharp"] = CSharp,
    ["cs"] = CSharp,
    ["c#"] = CSharp,
    ["javascript"] = JavaScript,
    ["js"] = JavaScript,
    ["typescript"] = JavaScript,
    ["ts"] = JavaScript,
    ["jsx"] = JavaScript,
    ["tsx"] = JavaScript,
    ["python"] = Python,
    ["py"] = Python,
    ["xml"] = XmlDef,
    ["html"] = XmlDef,
    ["xaml"] = XmlDef,
    ["axaml"] = XmlDef,
    ["svg"] = XmlDef,
    ["bash"] = Bash,
    ["sh"] = Bash,
    ["shell"] = Bash,
    ["zsh"] = Bash,
    ["json"] = JsonDef,
  };

  public static MarkdownCodeToken[] Tokenize(string code, string language)
  {
    ArgumentNullException.ThrowIfNull(code);
    ArgumentNullException.ThrowIfNull(language);
    if (code.Length == 0)
    {
      return [];
    }

    if (!Languages.TryGetValue(language, out LangDef? lang))
    {
      return [new MarkdownCodeToken(code, MarkdownCodeTokenKind.Default)];
    }

    List<MarkdownCodeToken> tokens = [];
    StringBuilder plain = new();
    int i = 0;
    while (i < code.Length)
    {
      if (ConsumeBlockComment(code, ref i, lang, tokens, plain))
      {
        continue;
      }
      if (lang.UseXmlMode && code[i] == '<')
      {
        Flush(plain, tokens);
        i = TokenXmlTag(code, i, tokens);
        continue;
      }
      if (ConsumeLineComment(code, ref i, lang, tokens, plain))
      {
        continue;
      }
      if (lang.HasVerbatimString && code[i] == '@' && i + 1 < code.Length && Array.IndexOf(lang.StringDelims, code[i + 1]) >= 0)
      {
        Flush(plain, tokens);
        i = TokenVerbatimString(code, i, tokens);
        continue;
      }
      if (Array.IndexOf(lang.StringDelims, code[i]) >= 0)
      {
        Flush(plain, tokens);
        i = TokenString(code, i, tokens);
        continue;
      }
      if (char.IsDigit(code[i]))
      {
        Flush(plain, tokens);
        i = TokenNumber(code, i, tokens);
        continue;
      }
      if (IsWordChar(code[i]))
      {
        int start = i;
        while (i < code.Length && IsWordChar(code[i]))
        {
          i++;
        }
        Flush(plain, tokens);
        string word = code[start..i];
        tokens.Add(lang.Keywords.Contains(word)
            ? new MarkdownCodeToken(word, MarkdownCodeTokenKind.Keyword)
            : new MarkdownCodeToken(word, MarkdownCodeTokenKind.Default));
        continue;
      }
      _ = plain.Append(code[i]);
      i++;
    }

    Flush(plain, tokens);
    return [.. tokens];
  }

  /// <summary>Consumes a block comment starting at i when one begins there; an
  ///     unterminated comment runs to end of text. Returns false when no opener.</summary>
  private static bool ConsumeBlockComment(string code, ref int i, LangDef lang,
      List<MarkdownCodeToken> tokens, StringBuilder plain)
  {
    if (lang.BlockComment.Length != 2 || !code.AsSpan(i).StartsWith(lang.BlockComment[0], StringComparison.Ordinal))
    {
      return false;
    }

    Flush(plain, tokens);
    int end = code.IndexOf(lang.BlockComment[1], i + lang.BlockComment[0].Length, StringComparison.Ordinal);
    int stop = end < 0 ? code.Length : end + lang.BlockComment[1].Length;
    tokens.Add(new MarkdownCodeToken(code[i..stop], MarkdownCodeTokenKind.Comment));
    i = stop;
    return true;
  }

  /// <summary>Consumes a line comment starting at i when one begins there; the
  ///     newline itself stays plain (layout, not content). Returns false otherwise.</summary>
  private static bool ConsumeLineComment(string code, ref int i, LangDef lang,
      List<MarkdownCodeToken> tokens, StringBuilder plain)
  {
    int pos = i; // local copy: ref parameters cannot cross into the lambda below
    if (!Array.Exists(lang.LineCommentPrefixes, p => code.AsSpan(pos).StartsWith(p, StringComparison.Ordinal)))
    {
      return false;
    }

    Flush(plain, tokens);
    int nl = code.IndexOf('\n', i);
    int stop = nl < 0 ? code.Length : nl;
    tokens.Add(new MarkdownCodeToken(code[i..stop], MarkdownCodeTokenKind.Comment));
    i = stop;
    return true;
  }

  /// <summary>Colors tag names, attribute names and the angle/slash brackets as
  ///     keywords; attribute values as strings. Stops after the matching close.
  ///     A nested open-bracket bails back to the main scanner (malformed nesting).</summary>
  private static int TokenXmlTag(string code, int start, List<MarkdownCodeToken> tokens)
  {
    int i = start + 1; // past the opening '<'
    int nameStart = i;
    while (i < code.Length && IsNameChar(code[i]))
    {
      i++;
    }

    string name = code[nameStart..i];
    tokens.Add(new MarkdownCodeToken("<", MarkdownCodeTokenKind.Keyword));
    if (name.Length > 0)
    {
      tokens.Add(new MarkdownCodeToken(name, MarkdownCodeTokenKind.Keyword));
    }

    StringBuilder plain = new();
    while (i < code.Length)
    {
      char c = code[i];
      if (c is Dq or Sq)
      {
        Flush(plain, tokens);
        int s = i;
        i++;
        while (i < code.Length && code[i] != c)
        {
          i++;
        }

        if (i < code.Length)
        {
          i++;
        }

        tokens.Add(new MarkdownCodeToken(code[s..i], MarkdownCodeTokenKind.String));
      }
      else if (c == '>' || (c == '/' && i + 1 < code.Length && code[i + 1] == '>'))
      {
        Flush(plain, tokens);
        int len = c == '>' ? 1 : 2;
        tokens.Add(new MarkdownCodeToken(code[i..(i + len)], MarkdownCodeTokenKind.Keyword));
        return i + len;
      }
      else if (c == '<')
      {
        Flush(plain, tokens);
        return i;
      }
      else if (IsNameChar(c))
      {
        Flush(plain, tokens);
        int s = i;
        while (i < code.Length && IsNameChar(code[i]))
        {
          i++;
        }

        tokens.Add(new MarkdownCodeToken(code[s..i], MarkdownCodeTokenKind.Keyword));
      }
      else
      {
        _ = plain.Append(c);
        i++;
      }
    }

    Flush(plain, tokens);
    return i;
  }

  /// <summary>Verbatim quotes (at-sign prefixed) and template literals carry no
  ///     escapes: the literal ends at the next matching delimiter.</summary>
  private static int TokenVerbatimString(string code, int start, List<MarkdownCodeToken> tokens)
  {
    char delim = code[start + 1];
    int i = start + 2;
    while (i < code.Length && code[i] != delim)
    {
      i++;
    }

    if (i < code.Length)
    {
      i++;
    }

    tokens.Add(new MarkdownCodeToken(code[start..i], MarkdownCodeTokenKind.String));
    return i;
  }

  private static int TokenString(string code, int start, List<MarkdownCodeToken> tokens)
  {
    char delim = code[start];
    int i = start + 1;
    while (i < code.Length)
    {
      if (code[i] == Backslash && delim != Backtick && i + 1 < code.Length)
      {
        i += 2; // escaped character: skip both
        continue;
      }

      if (code[i] == delim)
      {
        i++;
        break;
      }

      i++;
    }

    tokens.Add(new MarkdownCodeToken(code[start..i], MarkdownCodeTokenKind.String));
    return i;
  }

  private static int TokenNumber(string code, int start, List<MarkdownCodeToken> tokens)
  {
    int i = start;
    while (i < code.Length && (char.IsDigit(code[i]) || code[i] is '.' or 'e' or 'E' or 'x' or 'X' or 'b' or 'B' or 'a' or 'f' or 'A' or 'd' or 'D' or '_'))
    {
      i++;
    }

    tokens.Add(new MarkdownCodeToken(code[start..i], MarkdownCodeTokenKind.Number));
    return i;
  }

  private static void Flush(StringBuilder plain, List<MarkdownCodeToken> tokens)
  {
    if (plain.Length > 0)
    {
      tokens.Add(new MarkdownCodeToken(plain.ToString(), MarkdownCodeTokenKind.Default));
      _ = plain.Clear();
    }
  }

  private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

  private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is ':' or '-' or '_' or '.';
}
