namespace eThangAgent.FileSystem.ACL;

/// <summary>One parsed <c>.editorconfig</c>: the chain-root claim plus, per section,
///     every key/value pair that section declares. Later sections override earlier
///     ones during evaluation, which is what the editorconfig spec requires.</summary>
internal sealed class EditorConfigDocument
{
  private EditorConfigDocument(bool claimsRoot, IReadOnlyList<EditorConfigSection> sections)
  {
    ClaimsRoot = claimsRoot;
    Sections = sections;
  }

  /// <summary>True when the file declares <c>root = true</c> above any section, which
  ///     ends the upward chain at this directory.</summary>
  public bool ClaimsRoot { get; }

  /// <summary>The sections in file order; a file with entries before its first section
  ///     header keeps them in a leading section with a null glob.</summary>
  public IReadOnlyList<EditorConfigSection> Sections { get; }

  /// <summary>Parses editorconfig text. Comment lines start with '#' or ';'. Malformed
  ///     lines (no '=') are skipped: a config file's typos are never a load failure.</summary>
  public static EditorConfigDocument Parse(IReadOnlyList<string> lines)
  {
    bool claimsRoot = false;
    List<EditorConfigSection> sections = [];
    EditorConfigSection? current = null;

    foreach (string raw in lines)
    {
      string line = raw.Trim();
      if (line.Length == 0 || line[0] == '#' || line[0] == ';')
      {
        continue;
      }

      if (line[0] == '[' && line[^1] == ']')
      {
        current = new EditorConfigSection(line[1..^1]);
        sections.Add(current);
        continue;
      }

      int equals = line.IndexOf('=', StringComparison.Ordinal);
      if (equals < 0)
      {
        continue;
      }

      string key = line[..equals].Trim();
      string value = line[(equals + 1)..].Trim();
      if (current is null)
      {
        // Above the first section header: only the chain-root declaration matters.
        claimsRoot |= string.Equals(key, "root", StringComparison.OrdinalIgnoreCase)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        continue;
      }

      current.Set(key, value);
    }

    return new EditorConfigDocument(claimsRoot, sections);
  }
}

/// <summary>One <c>[glob]</c> block: its glob (null for leading pre-section entries)
///     and the properties it declares, case-insensitive by editorconfig convention.</summary>
internal sealed class EditorConfigSection(string? glob)
{
  private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>The section glob, or null for entries that precede any section header.</summary>
  public string? Glob { get; } = glob;

  public void Set(string key, string value) => _properties[key] = value;

  /// <summary>The declared value for <paramref name="key"/> in this section, if any.</summary>
  public bool TryGet(string key, out string value) => _properties.TryGetValue(key, out value!);
}
