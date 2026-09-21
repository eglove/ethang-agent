namespace eThangAgent.ComputerUse.Host;

/// <summary>Modifier flags of a key chord (task 17): the wire's ctrl|alt|shift|win
///     vocabulary, already mapped to the SendInput virtual-key space.</summary>
[Flags]
public enum KeyModifiers
{
  None = 0,
  Shift = 0x01,
  Control = 0x02,
  Alt = 0x04,
  Win = 0x08,
}

/// <summary>One normalized key chord: the virtual-key code plus the modifier set.
///     Produced only by <see cref="KeyChordNormalizer.Parse"/>.</summary>
public readonly record struct KeyChord(ushort KeyCode, KeyModifiers Modifiers);

/// <summary>The typed parse failure: the message names the offending chord or key so
///     the broker can answer invalid_request with a self-correctable message.</summary>
public sealed class KeyChordException : Exception
{
  /// <summary>Creates the typed chord failure with a default message.</summary>
  public KeyChordException() : this("key chord parse failed.") { }

  /// <summary>Creates the typed chord failure.</summary>
  public KeyChordException(string message) : base(message) { }

  /// <summary>Creates the typed chord failure with an inner cause.</summary>
  public KeyChordException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Parses chord text into SendInput-ready sequences (task 17). Accepted:
///     single short names (letters, digits, Return/Enter, Tab, Escape/Esc, Backspace,
///     Delete/Del, Insert, Home, End, PageUp/prior, PageDown/next, arrows, Space,
///     F1-F24) and X-keysym style (Control_L+a, super+c). Modifiers ctrl|alt|shift|win
///     join with plus; super aliases to win on Windows; a keysym side suffix (_L/_R) on
///     a modifier token describes the side, not a separate key. Unknown keys and
///     malformed glue are typed errors (invalid_request naming the key).</summary>
public static class KeyChordNormalizer
{
  private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
  {
    ["return"] = 0x0D,
    ["enter"] = 0x0D,
    ["tab"] = 0x09,
    ["escape"] = 0x1B,
    ["esc"] = 0x1B,
    ["backspace"] = 0x08,
    ["delete"] = 0x2E,
    ["del"] = 0x2E,
    ["insert"] = 0x2D,
    ["home"] = 0x24,
    ["end"] = 0x23,
    ["pageup"] = 0x21,
    ["prior"] = 0x21,
    ["pagedown"] = 0x22,
    ["next"] = 0x22,
    ["up"] = 0x26,
    ["down"] = 0x28,
    ["left"] = 0x25,
    ["right"] = 0x27,
    ["space"] = 0x20,
  };

  private static readonly Dictionary<string, KeyModifiers> NamedModifiers = new(StringComparer.OrdinalIgnoreCase)
  {
    ["ctrl"] = KeyModifiers.Control,
    ["control"] = KeyModifiers.Control,
    ["alt"] = KeyModifiers.Alt,
    ["shift"] = KeyModifiers.Shift,
    ["win"] = KeyModifiers.Win,
    ["super"] = KeyModifiers.Win,
    ["meta"] = KeyModifiers.Win,
  };

  /// <summary>Parses one chord; throws <see cref="KeyChordException"/> on anything
  ///     that does not match the grammar exactly.</summary>
  public static KeyChord Parse(string text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      throw new KeyChordException("key chord is empty; name a key, e.g. 'a', 'Return', 'ctrl+alt+Delete'.");
    }

    KeyModifiers modifiers = KeyModifiers.None;
    ushort keyCode = 0;
    bool hasKey = false;
    foreach (string rawToken in text.Split('+'))
    {
      string token = rawToken.Trim();
      if (token.Length == 0)
      {
        throw new KeyChordException($"empty token in key chord '{text}'.");
      }

      if (TryModifier(token, out KeyModifiers modifier))
      {
        if (hasKey)
        {
          throw new KeyChordException($"modifier '{token}' appears after the key in '{text}'; modifiers come first.");
        }

        modifiers |= modifier;
        continue;
      }

      if (hasKey)
      {
        throw new KeyChordException($"two keys in chord '{text}'; one key per request.");
      }

      keyCode = KeyFor(token, text);
      hasKey = true;
    }

    return hasKey
      ? new KeyChord(keyCode, modifiers)
      : throw new KeyChordException($"key chord '{text}' has modifiers but no key.");
  }

  /// <summary>Modifier tokens accept a keysym side suffix (Shift_L, Control_R): the
  ///     side names the physical key, not a different modifier.</summary>
  private static bool TryModifier(string token, out KeyModifiers modifier)
  {
    string bare = StripSideSuffix(token);
    return NamedModifiers.TryGetValue(bare, out modifier);
  }

  /// <summary>Strips a keysym SIDE suffix (_L/_R): it names the physical key of a
  ///     modifier, never a different modifier nor a separate key. Any other underscore
  ///     is malformed glue and stays in the token - the lookup then rejects it typed.</summary>
  private static string StripSideSuffix(string token)
  {
    return token.Length > 2 && token[^2] == '_' && token[^1] is 'L' or 'l' or 'R' or 'r'
      ? token[..^2]
      : token;
  }

  private static ushort KeyFor(string token, string chordText)
  {
    string bare = StripSideSuffix(token);
    if (bare.Length == 1)
    {
      char upper = char.ToUpperInvariant(bare[0]);
      if (upper is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
      {
        return upper;
      }
    }

    return TryGetNamedOrFunctionKey(bare, out ushort resolved)
      ? resolved
      : throw new KeyChordException($"unknown key '{token}' in chord '{chordText}': use a letter, digit, F1-F24, or a name like Return, Tab, Escape, Delete, PageUp.");
  }

  /// <summary>Resolves F1-F24 and every named short key; false when unknown.</summary>
  private static bool TryGetNamedOrFunctionKey(string bare, out ushort keyCode)
  {
    if (TryFunctionKey(bare, out ushort functionKey))
    {
      keyCode = functionKey;
      return true;
    }

    return NamedKeys.TryGetValue(bare, out keyCode);
  }


  /// <summary>Matches F1-F24 (case-insensitive) to its virtual-key code.</summary>
  private static bool TryFunctionKey(string bare, out ushort keyCode)
  {
    if (bare.Length is 2 or 3 && char.ToLowerInvariant(bare[0]) == 'f'
        && int.TryParse(bare[1..], out int fKey) && fKey is >= 1 and <= 24)
    {
      keyCode = (ushort)(0x70 + fKey - 1);
      return true;
    }

    keyCode = 0;
    return false;
  }
}
