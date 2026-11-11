namespace eThangAgent.ToolDomain;

/// <summary>Renders the co-author trailer an agent commit carries: the model serving
///     the turn that made the commit, and the GitHub org that publishes it.
///     <para>THE TRAILER CARRIES NO EMAIL, so GitHub resolves no account from it and
///     credits no contributor: the line is read in <c>git log</c> and nowhere else.
///     Credit GitHub counts needs an address on a real account, and a model has none.</para>
///     <para>EVERY FAILURE TO IDENTIFY A MODEL YIELDS NULL rather than a placeholder,
///     so a commit whose model is unknown carries no trailer at all.</para></summary>
public static class ModelAttribution
{
  /// <summary>Model ids that name no model: the provider's routing pseudo-model, whose
  ///     real serving model arrives separately whenever the provider names one.</summary>
  private static readonly HashSet<string> Unattributable =
      [with(StringComparer.OrdinalIgnoreCase), "openrouter/auto"];

  /// <summary>Vendor slug - the model id's leading segment - to the GitHub org that
  ///     publishes the model.</summary>
  private static readonly Dictionary<string, string> VendorOrgs =
      new(StringComparer.OrdinalIgnoreCase)
      {
        ["anthropic"] = "anthropics",
        ["openai"] = "openai",
        ["google"] = "google-deepmind",
        ["x-ai"] = "xai-org",
        ["meta-llama"] = "meta-llama",
        ["meta"] = "meta-llama",
        ["mistralai"] = "mistralai",
        ["deepseek"] = "deepseek-ai",
        ["qwen"] = "QwenLM",
        ["cohere"] = "cohere-ai",
        ["moonshotai"] = "MoonshotAI",
        ["minimax"] = "MiniMax-AI",
        ["z-ai"] = "zai-org",
        ["nvidia"] = "nvidia",
        ["amazon"] = "aws",
        ["microsoft"] = "microsoft",
        ["perplexity"] = "perplexity-ai",
        ["nousresearch"] = "NousResearch",
        ["allenai"] = "allenai",
        ["liquid"] = "liquid-ai",
        ["ai21"] = "ai21labs",
        ["upstage"] = "upstage",
        ["thudm"] = "THUDM",
        ["ibm-granite"] = "ibm-granite",
        ["arcee-ai"] = "arcee-ai",
        ["inception"] = "inceptionai",
        ["stepfun-ai"] = "stepfun-ai",
        ["inclusionai"] = "inclusionAI",
        ["baidu"] = "baidu",
        ["tencent"] = "tencent",
        ["bytedance"] = "bytedance",
      };

  /// <summary>Tokens whose published casing is not title case.</summary>
  private static readonly Dictionary<string, string> Casing =
      new(StringComparer.OrdinalIgnoreCase)
      {
        ["gpt"] = "GPT",
        ["glm"] = "GLM",
        ["deepseek"] = "DeepSeek",
        ["minimax"] = "MiniMax",
        ["vl"] = "VL",
        ["swe"] = "SWE",
        ["oss"] = "OSS",
      };

  /// <summary>The trailer line for <paramref name="modelId"/>, or null when the id
  ///     names no model. The org link is omitted when the vendor has no known org.</summary>
  public static string? TrailerLine(string? modelId)
  {
    string? name = DisplayName(modelId);
    if (name is null)
    {
      return null;
    }

    string? org = VendorOrg(modelId!);
    return org is null
        ? $"Co-authored-by: {name}"
        : $"Co-authored-by: {name} [https://github.com/{org}]";
  }

  /// <summary>The message with the trailer appended after a blank line, or the message
  ///     unchanged when no trailer applies. Either result ends in exactly one newline.</summary>
  public static string WithTrailer(string message, string? modelId)
  {
    ArgumentNullException.ThrowIfNull(message);
    string? line = TrailerLine(modelId);
    return line is null
        ? message
        : message.TrimEnd('\n') + "\n\n" + line + "\n";
  }

  /// <summary>The model's readable name, derived from the id's model segment: tokens
  ///     joined by spaces, a trailing date suffix dropped, and consecutive all-digit
  ///     tokens joined by dots into one version. Null when the id names no model.</summary>
  private static string? DisplayName(string? modelId)
  {
    if (string.IsNullOrWhiteSpace(modelId))
    {
      return null;
    }

    string id = modelId.Trim();
    if (Unattributable.Contains(id))
    {
      return null;
    }

    int slash = id.IndexOf('/', StringComparison.Ordinal);
    string segment = slash < 0 ? id : id[(slash + 1)..];
    if (segment.Length == 0)
    {
      return null;
    }

    string[] tokens = [.. segment.Split('-', StringSplitOptions.RemoveEmptyEntries)];
    return tokens.Length == 0 ? null : JoinName(WithoutDateSuffix(tokens));
  }

  /// <summary>The tokens with a trailing date suffix removed, so a dated release reads
  ///     as its version rather than as a date. A lone token is kept.</summary>
  private static string[] WithoutDateSuffix(string[] tokens) =>
      tokens.Length > 1 && IsDateSuffix(tokens[^1])
          ? [.. tokens[..^1]]
          : tokens;

  /// <summary>True for a four- or eight-digit run, the two date forms the catalog's
  ///     model ids carry.</summary>
  private static bool IsDateSuffix(string token) =>
      IsVersion(token) && token.Length is 4 or 8;

  /// <summary>Joins the tokens into a name: consecutive all-digit tokens become one
  ///     dotted version, and every other token is cased.</summary>
  private static string JoinName(string[] tokens)
  {
    List<string> words = [with(tokens.Length)];
    foreach (string token in tokens)
    {
      if (words.Count > 0 && IsVersion(token) && IsVersion(words[^1]))
      {
        words[^1] = words[^1] + "." + token;
        continue;
      }

      words.Add(Cased(token));
    }

    return string.Join(' ', words);
  }

  private static bool IsVersion(string token) =>
      token.Length > 0 && token.All(char.IsAsciiDigit);

  private static string Cased(string token) =>
      Casing.TryGetValue(token, out string? cased)
          ? cased
          : char.ToUpperInvariant(token[0]) + token[1..];

  /// <summary>The GitHub org for the id's vendor segment, or null when the vendor has
  ///     no known org or the id names no vendor.</summary>
  private static string? VendorOrg(string modelId)
  {
    int slash = modelId.IndexOf('/', StringComparison.Ordinal);
    return slash <= 0 ? null : VendorOrgs.GetValueOrDefault(modelId[..slash]);
  }
}
