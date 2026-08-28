using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Strict JSON-to-model boundary for <see cref="MarkdownDocument"/>. Every
/// validation lives here: unknown block types and fields are rejected, field types are
/// exact, header levels are 1-3, table rows must match header count, frontmatter values
/// must be string/bool/number without newlines. Nothing is silently coerced, defaulted,
/// or clamped - a malformed document never reaches <see cref="MarkdownRenderer"/>, whose
/// rendering is therefore total.</summary>
public static class MarkdownDocumentParser
{
  private const string Items = "items";

  private static readonly HashSet<string> AlertWords = new(StringComparer.Ordinal)
    { "CAUTION", "IMPORTANT", "NOTE", "TIP", "WARNING" };

  private static Result<MarkdownDocument> Fail(DomainError e) => Result.Failure<MarkdownDocument>(e);

  private static AlertType WordToAlert(string word) => word switch
  {
    "CAUTION" => AlertType.Caution,
    "IMPORTANT" => AlertType.Important,
    "NOTE" => AlertType.Note,
    "TIP" => AlertType.Tip,
    "WARNING" => AlertType.Warning,
    _ => throw new InvalidOperationException("unreachable: validated by caller"),
  };

  public static Result<MarkdownDocument> Parse(JsonElement root) =>
      Parse(root, "document");

  public static Result<MarkdownDocument> Parse(JsonElement root, string source)
  {
    if (root.ValueKind != JsonValueKind.Object)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterType, $"'{source}' must be a JSON object, but got {root.ValueKind}."));
    }

    if (!root.TryGetProperty("blocks", out JsonElement blocksEl))
    {
      return Fail(new DomainError(ToolErrorCodes.MissingParameter, $"'{source}.blocks' is required."));
    }

    if (blocksEl.ValueKind != JsonValueKind.Array)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterType, $"'{source}.blocks' must be an array, but got {blocksEl.ValueKind}."));
    }

    Result<(List<MarkdownBlock?> Blocks, IReadOnlyDictionary<string, object>? FrontMatter)> parsed =
        ParseBlocksAndFrontMatter(root, blocksEl, source);
    if (!parsed.IsSuccess)
    {
      return Fail(parsed.Error!);
    }

    MarkdownDocument document = new(parsed.Value.Blocks, parsed.Value.FrontMatter);
    return Result.Success(document);
  }

  private static Result<(List<MarkdownBlock?> Blocks, IReadOnlyDictionary<string, object>? FrontMatter)>
      ParseBlocksAndFrontMatter(JsonElement root, JsonElement blocksEl, string source)
  {
    List<MarkdownBlock?> blocks = [];
    int index = 0;
    foreach (JsonElement b in blocksEl.EnumerateArray())
    {
      Result<MarkdownBlock?> parsed = ParseBlock(b);
      if (!parsed.IsSuccess)
      {
        DomainError wrapped = new(parsed.Error!.Code, $"block[{index}]: {parsed.Error.Message}");
        return FailBlocksAndFrontMatter(wrapped);
      }

      blocks.Add(parsed.Value!);
      index++;
    }

    Result<IReadOnlyDictionary<string, object>?> frontMatter = ParseFrontMatter(root, source);
    Result<(List<MarkdownBlock?> Blocks, IReadOnlyDictionary<string, object>? FrontMatter)> result = frontMatter.IsSuccess
      ? Result.Success((blocks, frontMatter.Value))
      : FailBlocksAndFrontMatter(frontMatter.Error!);
    return result;
  }

  private static Result<IReadOnlyDictionary<string, object>?> ParseFrontMatter(JsonElement root, string source)
  {
    if (!root.TryGetProperty("frontmatter", out JsonElement fmEl))
    {
      return Result.Success<IReadOnlyDictionary<string, object>?>(null);
    }

    if (fmEl.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<IReadOnlyDictionary<string, object>?>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{source}.frontmatter' must be an object, but got {fmEl.ValueKind}."));
    }

    Dictionary<string, object> fm = [];
    foreach (JsonProperty p in fmEl.EnumerateObject())
    {
      DomainError? invalid = AddFrontMatterValue(fm, p, source);
      if (invalid is not null)
      {
        return Result.Failure<IReadOnlyDictionary<string, object>?>(invalid);
      }
    }

    return Result.Success<IReadOnlyDictionary<string, object>?>(fm);
  }

  /// <summary>Adds one frontmatter entry: string / number / boolean only, no newlines.</summary>
  private static DomainError? AddFrontMatterValue(Dictionary<string, object> fm, JsonProperty p, string source)
  {
    switch (p.Value.ValueKind)
    {
      case JsonValueKind.String:
        fm.Add(p.Name, p.Value.GetString()!);
        break;
      case JsonValueKind.Number:
        fm.Add(p.Name, p.Value.GetDouble());
        break;
      case JsonValueKind.True or JsonValueKind.False:
        fm.Add(p.Name, p.Value.GetBoolean());
        break;
      case JsonValueKind.Null:
        return new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'{source}.frontmatter.{p.Name}' must not be null; omit it instead.");
      case JsonValueKind.Object:
      case JsonValueKind.Array:
      case JsonValueKind.Undefined:
      default:
        return new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'{source}.frontmatter.{p.Name}' must be a string, number, or boolean, but got {p.Value.ValueKind}.");
    }

    DomainError? newline = fm[p.Name] is string s && s.Contains('\n', StringComparison.Ordinal)
      ? new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"Frontmatter value for '{p.Name}' contains a newline; multi-line values are not allowed.")
      : null;
    return newline;
  }

  private static Result<MarkdownBlock?> ParseBlock(JsonElement b)
  {
    if (b.ValueKind == JsonValueKind.Null)
    {
      return Result.Success<MarkdownBlock?>(null);
    }

    if (b.ValueKind != JsonValueKind.Object)
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterType, $"each block must be an object or null, but got {b.ValueKind}."));
    }

    if (!b.TryGetProperty("type", out JsonElement typeEl))
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.MissingParameter, "each block requires a 'type'."));
    }

    if (typeEl.ValueKind != JsonValueKind.String)
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterType, $"'type' must be a string, but got {typeEl.ValueKind}."));
    }

    string type = typeEl.GetString()!;
    HashSet<string>? known = TypeFields(type);
    if (known is null)
    {
      return FailMarkdownBlock(new DomainError("UnknownParameter", $"unknown block type '{type}'."));
    }

    // Unknown-field rejection: every property must be declared for this block type.
    string? unknown = b.EnumerateObject()
        .Select(p => p.Name)
        .FirstOrDefault(name => name != "type" && !known.Contains(name));
    return unknown is not null
      ? FailMarkdownBlock(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {unknown}. Allowed for '{type}': {string.Join(", ", known.OrderBy(k => k))}."))
      : type switch
      {
        "text" => RequireText(b, "text").Map(text => (MarkdownBlock?)new TextBlock(text)),
        "header" => ParseHeader(b),
        "quote" => RequireText(b, "text").Map(text => (MarkdownBlock?)new QuoteBlock(text)),
        "alert" => ParseAlert(b),
        "codeBlock" => ParseCodeBlock(b),
        "space" => ParseSpace(b),
        "unorderedList" or "numberedList" => ParseList(b, type),
        "taskList" => ParseTaskList(b),
        "table" => ParseTable(b),
        _ => FailMarkdownBlock(new DomainError("UnknownParameter", $"unknown block type '{type}'.")),
      };
  }

  private static Result<MarkdownBlock?> ParseHeader(JsonElement b)
  {
    if (!b.TryGetProperty("level", out JsonElement levelEl))
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.MissingParameter, "'header' blocks require 'level'."));
    }

    if (levelEl.ValueKind != JsonValueKind.Number || !levelEl.TryGetInt32(out int level))
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterType, "'header.level' must be an integer."));
    }

    if (level is < 1 or > 3)
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterValue, $"'header.level' must be 1-3, but got {level}."));
    }

    Result<string> text = RequireText(b, "text");
    return text.IsSuccess
        ? Result.Success<MarkdownBlock?>(new HeaderBlock(level, text.Value!))
        : FailMarkdownBlock(text.Error!);
  }

  private static Result<MarkdownBlock?> ParseAlert(JsonElement b)
  {
    if (!b.TryGetProperty("alertType", out JsonElement alertEl))
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.MissingParameter, "'alert' blocks require 'alertType'."));
    }

    if (alertEl.ValueKind != JsonValueKind.String)
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterType, "'alert.alertType' must be a string."));
    }

    string word = alertEl.GetString()!;
    if (!AlertWords.Contains(word))
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'alert.alertType' must be one of CAUTION, IMPORTANT, NOTE, TIP, WARNING, but got '{word}'."));
    }

    Result<string> text = RequireText(b, "text");
    return text.IsSuccess
        ? Result.Success<MarkdownBlock?>(new AlertBlock(WordToAlert(word), text.Value!))
        : FailMarkdownBlock(text.Error!);
  }

  private static Result<MarkdownBlock?> ParseCodeBlock(JsonElement b)
  {
    Result<string> code = RequireText(b, "code");
    if (!code.IsSuccess)
    {
      return FailMarkdownBlock(code.Error!);
    }

    string? language = null;
    if (b.TryGetProperty("language", out JsonElement langEl))
    {
      if (langEl.ValueKind != JsonValueKind.String)
      {
        return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterType, "'codeBlock.language' must be a string."));
      }

      language = langEl.GetString();
    }
    return Result.Success<MarkdownBlock?>(new CodeBlock(code.Value!, language));
  }

  private static Result<MarkdownBlock?> ParseSpace(JsonElement b)
  {
    int count = 1;
    if (b.TryGetProperty("count", out JsonElement countEl))
    {
      if (countEl.ValueKind != JsonValueKind.Number || !countEl.TryGetInt32(out count))
      {
        return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterType, "'space.count' must be an integer."));
      }

      if (count < 1)
      {
        return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterValue, "'space.count' must be >= 1."));
      }
    }
    return Result.Success<MarkdownBlock?>(new SpaceBlock(count));
  }

  private static Result<MarkdownBlock?> ParseList(JsonElement b, string type)
  {
    if (!b.TryGetProperty(Items, out JsonElement itemsEl))
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.MissingParameter, $"'{type}' blocks require 'items'."));
    }

    if (itemsEl.ValueKind != JsonValueKind.Array)
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterType, $"'{type}.items' must be an array."));
    }

    Result<IReadOnlyList<ListItem>> items = ParseListItems(itemsEl);
    if (!items.IsSuccess)
    {
      return FailMarkdownBlock(items.Error!);
    }

    ListKind kind = type == "numberedList" ? ListKind.Numbered : ListKind.Unordered;
    return Result.Success<MarkdownBlock?>(new ListBlock(kind, items.Value!));
  }

  private static Result<IReadOnlyList<ListItem>> ParseListItems(JsonElement arr) =>
      ParseListItems(arr, depth: 0);

  private static Result<IReadOnlyList<ListItem>> ParseListItems(JsonElement arr, int depth)
  {
    if (depth > 16)
    {
      return FailList(new DomainError(ToolErrorCodes.InvalidParameterValue, "list nesting exceeds the maximum depth of 16."));
    }

    List<ListItem> items = [];
    foreach (JsonElement el in arr.EnumerateArray())
    {
      Result<ListItem> item = ParseListItem(el, depth);
      if (!item.IsSuccess)
      {
        return FailList(item.Error!);
      }

      items.Add(item.Value!);
    }

    return Result.Success<IReadOnlyList<ListItem>>(items);
  }

  /// <summary>One list entry: object with non-empty string 'text' and optional
  ///     'children' (recursing one level deeper).</summary>
  private static Result<ListItem> ParseListItem(JsonElement el, int depth)
  {
    if (el.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<ListItem>(new DomainError(ToolErrorCodes.InvalidParameterType, $"each list item must be an object, but got {el.ValueKind}."));
    }

    if (!el.TryGetProperty("text", out JsonElement textEl))
    {
      return Result.Failure<ListItem>(new DomainError(ToolErrorCodes.MissingParameter, "each list item requires 'text'."));
    }

    if (textEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<ListItem>(new DomainError(ToolErrorCodes.InvalidParameterType, $"list item 'text' must be a string, but got {textEl.ValueKind}."));
    }

    string text = textEl.GetString()!;
    if (text.Length == 0)
    {
      return Result.Failure<ListItem>(new DomainError(ToolErrorCodes.InvalidParameterValue, "list item 'text' must not be empty."));
    }

    Result<IReadOnlyList<ListItem>?> children = ParseItemChildren(el, depth);
    if (!children.IsSuccess)
    {
      return Result.Failure<ListItem>(children.Error!);
    }

    ListItem item = new(text, children.Value);
    return Result.Success(item);
  }

  private static Result<IReadOnlyList<ListItem>?> ParseItemChildren(JsonElement el, int depth)
  {
    if (!el.TryGetProperty("children", out JsonElement childrenEl))
    {
      return Result.Success<IReadOnlyList<ListItem>?>(null);
    }

    if (childrenEl.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<ListItem>?>(new DomainError(ToolErrorCodes.InvalidParameterType, "list item 'children' must be an array."));
    }

    Result<IReadOnlyList<ListItem>> kids = ParseListItems(childrenEl, depth + 1);
    Result<IReadOnlyList<ListItem>?> result = kids.IsSuccess
      ? Result.Success(kids.Value)
      : Result.Failure<IReadOnlyList<ListItem>?>(kids.Error!);
    return result;
  }

  private static Result<MarkdownBlock?> ParseTaskList(JsonElement b)
  {
    if (!b.TryGetProperty(Items, out JsonElement itemsEl))
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.MissingParameter, "'taskList' blocks require 'items'."));
    }

    if (itemsEl.ValueKind != JsonValueKind.Array)
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterType, "'taskList.items' must be an array."));
    }

    List<TaskListItem> items = [];
    foreach (JsonElement el in itemsEl.EnumerateArray())
    {
      if (el.ValueKind != JsonValueKind.Object)
      {
        return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterType, $"each taskList item must be an object, but got {el.ValueKind}."));
      }

      if (!el.TryGetProperty("label", out JsonElement labelEl) || labelEl.ValueKind != JsonValueKind.String)
      {
        return FailMarkdownBlock(new DomainError(ToolErrorCodes.MissingParameter, "each taskList item requires a string 'label'."));
      }

      if (!el.TryGetProperty("isComplete", out JsonElement doneEl) || doneEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
      {
        return FailMarkdownBlock(new DomainError(ToolErrorCodes.MissingParameter, "each taskList item requires boolean 'isComplete'."));
      }

      items.Add(new TaskListItem(doneEl.GetBoolean(), labelEl.GetString()!));
    }
    return Result.Success<MarkdownBlock?>(new TaskListBlock(items));
  }

  private static Result<MarkdownBlock?> ParseTable(JsonElement b)
  {
    if (!b.TryGetProperty("headers", out JsonElement headersEl) || headersEl.ValueKind != JsonValueKind.Array)
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.MissingParameter, "'table' blocks require a non-empty 'headers' array."));
    }

    if (!headersEl.EnumerateArray().Any())
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterValue, "'table.headers' must have at least one column."));
    }

    if (!b.TryGetProperty("rows", out JsonElement rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
    {
      return FailMarkdownBlock(new DomainError(ToolErrorCodes.InvalidParameterType, "'table.rows' must be an array of arrays."));
    }

    Result<(List<TableHeader> Headers, List<IReadOnlyList<string>> Rows)> table = ParseTableBody(headersEl, rowsEl);
    if (!table.IsSuccess)
    {
      return FailMarkdownBlock(table.Error!);
    }

    MarkdownBlock block = new TableBlock(table.Value.Headers, table.Value.Rows);
    return Result.Success<MarkdownBlock?>(block);
  }

  private static Result<(List<TableHeader> Headers, List<IReadOnlyList<string>> Rows)> ParseTableBody(
      JsonElement headersEl, JsonElement rowsEl)
  {
    Result<List<TableHeader>> headers = ParseTableHeaders(headersEl);
    if (!headers.IsSuccess)
    {
      return Result.Failure<(List<TableHeader>, List<IReadOnlyList<string>>)>(headers.Error!);
    }

    Result<List<IReadOnlyList<string>>> rows = ParseTableRows(rowsEl, headers.Value!.Count);
    Result<(List<TableHeader> Headers, List<IReadOnlyList<string>> Rows)> table = rows.IsSuccess
      ? Result.Success((headers.Value!, rows.Value!))
      : Result.Failure<(List<TableHeader>, List<IReadOnlyList<string>>)>(rows.Error!);
    return table;
  }

  private static Result<List<TableHeader>> ParseTableHeaders(JsonElement headersEl)
  {
    List<TableHeader> headers = [];
    foreach (JsonElement h in headersEl.EnumerateArray())
    {
      if (h.ValueKind == JsonValueKind.String)
      {
        headers.Add(new TableHeader(h.GetString()!));
        continue;
      }

      DomainError? invalid = ParseObjectHeader(h, headers);
      if (invalid is not null)
      {
        return Result.Failure<List<TableHeader>>(invalid);
      }
    }

    return Result.Success(headers);
  }

  /// <summary>Parses one object-form table header ('text' plus optional 'align'),
  ///     appending it to <paramref name="headers"/> when valid.</summary>
  private static DomainError? ParseObjectHeader(JsonElement h, List<TableHeader> headers)
  {
    if (h.ValueKind != JsonValueKind.Object)
    {
      return new DomainError(ToolErrorCodes.InvalidParameterType,
          "each table header must be a string or an object with 'text' (+optional 'align').");
    }

    if (!h.TryGetProperty("text", out JsonElement ht) || ht.ValueKind != JsonValueKind.String)
    {
      return new DomainError(ToolErrorCodes.MissingParameter, "object table headers require a string 'text'.");
    }

    Result<TableAlign?> align = ParseHeaderAlign(h);
    if (!align.IsSuccess)
    {
      return align.Error!;
    }

    headers.Add(new TableHeader(ht.GetString()!, align.Value));
    return null;
  }

  private const string AlignRequirement = "'align' must be \"left\", \"center\", or \"right\".";

  private static Result<TableAlign?> ParseHeaderAlign(JsonElement h)
  {
    if (!h.TryGetProperty("align", out JsonElement alignEl))
    {
      return Result.Success<TableAlign?>(null);
    }

    if (alignEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<TableAlign?>(new DomainError(ToolErrorCodes.InvalidParameterType, AlignRequirement));
    }

    TableAlign? align = alignEl.GetString() switch
    {
      "left" => TableAlign.Left,
      "center" => TableAlign.Center,
      "right" => TableAlign.Right,
      _ => null,
    };
    Result<TableAlign?> result = align is not null
      ? Result.Success(align)
      : Result.Failure<TableAlign?>(new DomainError(ToolErrorCodes.InvalidParameterValue, AlignRequirement));
    return result;
  }

  private static Result<List<IReadOnlyList<string>>> ParseTableRows(JsonElement rowsEl, int headerCount)
  {
    List<IReadOnlyList<string>> rows = [];
    foreach (JsonElement rowEl in rowsEl.EnumerateArray())
    {
      if (rowEl.ValueKind != JsonValueKind.Array)
      {
        return Result.Failure<List<IReadOnlyList<string>>>(new DomainError(ToolErrorCodes.InvalidParameterType, "each table row must be an array of strings."));
      }

      DomainError? invalid = ParseTableRow(rowEl, headerCount, rows);
      if (invalid is not null)
      {
        return Result.Failure<List<IReadOnlyList<string>>>(invalid);
      }
    }

    return Result.Success(rows);
  }

  /// <summary>One table row: array of string cells, exactly one per header.</summary>
  private static DomainError? ParseTableRow(JsonElement rowEl, int headerCount, List<IReadOnlyList<string>> rows)
  {
    List<string> cells = [];
    foreach (JsonElement c in rowEl.EnumerateArray())
    {
      if (c.ValueKind != JsonValueKind.String)
      {
        return new DomainError(ToolErrorCodes.InvalidParameterType, "each table cell must be a string.");
      }

      cells.Add(c.GetString()!);
    }

    if (cells.Count != headerCount)
    {
      return new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"Table row cell count ({cells.Count}) does not match header count ({headerCount}).");
    }

    rows.Add(cells);
    return null;
  }

  private static Result<string> RequireText(JsonElement b, string field)
  {
    if (!b.TryGetProperty(field, out JsonElement el))
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.MissingParameter, $"'{field}' is required."));
    }

    if (el.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType, $"'{field}' must be a string, but got {el.ValueKind}."));
    }

    string text = el.GetString()!;
    return Result.Success(text);
  }

  private static HashSet<string>? TypeFields(string type) => type switch
  {
    "text" => new HashSet<string>(StringComparer.Ordinal) { "text" },
    "header" => new HashSet<string>(StringComparer.Ordinal) { "level", "text" },
    "quote" => new HashSet<string>(StringComparer.Ordinal) { "text" },
    "alert" => new HashSet<string>(StringComparer.Ordinal) { "alertType", "text" },
    "codeBlock" => new HashSet<string>(StringComparer.Ordinal) { "language", "code" },
    "space" => new HashSet<string>(StringComparer.Ordinal) { "count" },
    "unorderedList" or "numberedList" => new HashSet<string>(StringComparer.Ordinal) { Items },
    "taskList" => new HashSet<string>(StringComparer.Ordinal) { Items },
    "table" => new HashSet<string>(StringComparer.Ordinal) { "headers", "rows" },
    _ => null,
  };

  private static Result<MarkdownBlock?> FailMarkdownBlock(DomainError e) => Result.Failure<MarkdownBlock?>(e);
  private static Result<IReadOnlyList<ListItem>> FailList(DomainError e) => Result.Failure<IReadOnlyList<ListItem>>(e);
  private static Result<(List<MarkdownBlock?> Blocks, IReadOnlyDictionary<string, object>? FrontMatter)>
      FailBlocksAndFrontMatter(DomainError e)
      => Result.Failure<(List<MarkdownBlock?>, IReadOnlyDictionary<string, object>?)>(e);
}
