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
    private static readonly HashSet<string> AlertWords = new(StringComparer.Ordinal)
    { "CAUTION", "IMPORTANT", "NOTE", "TIP", "WARNING" };

    private static Result<MarkdownDocument> Fail(Error e) => Result<MarkdownDocument>.Failure(e);

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
            return Fail(new Error("InvalidParameterType", $"'{source}' must be a JSON object, but got {root.ValueKind}."));
        if (!root.TryGetProperty("blocks", out var blocksEl))
            return Fail(new Error("MissingParameter", $"'{source}.blocks' is required."));
        if (blocksEl.ValueKind != JsonValueKind.Array)
            return Fail(new Error("InvalidParameterType", $"'{source}.blocks' must be an array, but got {blocksEl.ValueKind}."));

        var blocks = new List<IMarkdownBlock?>();
        var index = 0;
        foreach (var b in blocksEl.EnumerateArray())
        {
            var parsed = ParseBlock(b);
            if (!parsed.IsSuccess)
                return Fail(new Error(parsed.Error!.Code, $"block[{index}]: {parsed.Error.Message}"));
            blocks.Add(parsed.Value!);
            index++;
        }

        IReadOnlyDictionary<string, object>? frontMatter = null;
        if (root.TryGetProperty("frontmatter", out var fmEl))
        {
            if (fmEl.ValueKind != JsonValueKind.Object)
                return Fail(new Error("InvalidParameterType", $"'{source}.frontmatter' must be an object, but got {fmEl.ValueKind}."));
            var fm = new Dictionary<string, object>();
            foreach (var p in fmEl.EnumerateObject())
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
                        return Fail(new Error("InvalidParameterValue",
                            $"'{source}.frontmatter.{p.Name}' must not be null; omit it instead."));
                    default:
                        return Fail(new Error("InvalidParameterType",
                            $"'{source}.frontmatter.{p.Name}' must be a string, number, or boolean, but got {p.Value.ValueKind}."));
                }
                if (fm[p.Name] is string s && s.Contains('\n'))
                    return Fail(new Error("InvalidParameterValue",
                        $"Frontmatter value for '{p.Name}' contains a newline; multi-line values are not allowed."));
            }
            frontMatter = fm;
        }

        return Result<MarkdownDocument>.Success(new MarkdownDocument(blocks, frontMatter));
    }

    private static Result<IMarkdownBlock?> ParseBlock(JsonElement b)
    {
        if (b.ValueKind == JsonValueKind.Null)
            return Result<IMarkdownBlock?>.Success(null);

        if (b.ValueKind != JsonValueKind.Object)
            return FailIMarkdownBlock(new Error("InvalidParameterType", $"each block must be an object or null, but got {b.ValueKind}."));

        if (!b.TryGetProperty("type", out var typeEl))
            return FailIMarkdownBlock(new Error("MissingParameter", "each block requires a 'type'."));
        if (typeEl.ValueKind != JsonValueKind.String)
            return FailIMarkdownBlock(new Error("InvalidParameterType", $"'type' must be a string, but got {typeEl.ValueKind}."));

        var type = typeEl.GetString()!;
        var known = TypeFields(type);
        if (known is null)
            return FailIMarkdownBlock(new Error("UnknownParameter", $"unknown block type '{type}'."));

        // Unknown-field rejection: every property must be declared for this block type.
        foreach (var p in b.EnumerateObject())
        {
            if (p.Name != "type" && !known.Contains(p.Name))
                return FailIMarkdownBlock(new Error("UnknownParameter",
                    $"Unknown parameter(s): {p.Name}. Allowed for '{type}': {string.Join(", ", known.OrderBy(k => k))}."));
        }

        return type switch
        {
            "text" => RequireText(b, "text").Map(text => (IMarkdownBlock?)new TextBlock(text)),
            "header" => ParseHeader(b),
            "quote" => RequireText(b, "text").Map(text => (IMarkdownBlock?)new QuoteBlock(text)),
            "alert" => ParseAlert(b),
            "codeBlock" => ParseCodeBlock(b),
            "space" => ParseSpace(b),
            "unorderedList" or "numberedList" => ParseList(b, type),
            "taskList" => ParseTaskList(b),
            "table" => ParseTable(b),
            _ => FailIMarkdownBlock(new Error("UnknownParameter", $"unknown block type '{type}'.")),
        };
    }

    private static Result<IMarkdownBlock?> ParseHeader(JsonElement b)
    {
        if (!b.TryGetProperty("level", out var levelEl))
            return FailIMarkdownBlock(new Error("MissingParameter", "'header' blocks require 'level'."));
        if (levelEl.ValueKind != JsonValueKind.Number || !levelEl.TryGetInt32(out var level))
            return FailIMarkdownBlock(new Error("InvalidParameterType", "'header.level' must be an integer."));
        if (level is < 1 or > 3)
            return FailIMarkdownBlock(new Error("InvalidParameterValue", $"'header.level' must be 1-3, but got {level}."));
        var text = RequireText(b, "text");
        return text.IsSuccess
            ? Result<IMarkdownBlock?>.Success(new HeaderBlock(level, text.Value!))
            : FailIMarkdownBlock(text.Error!);
    }

    private static Result<IMarkdownBlock?> ParseAlert(JsonElement b)
    {
        if (!b.TryGetProperty("alertType", out var alertEl))
            return FailIMarkdownBlock(new Error("MissingParameter", "'alert' blocks require 'alertType'."));
        if (alertEl.ValueKind != JsonValueKind.String)
            return FailIMarkdownBlock(new Error("InvalidParameterType", "'alert.alertType' must be a string."));
        var word = alertEl.GetString()!;
        if (!AlertWords.Contains(word))
            return FailIMarkdownBlock(new Error("InvalidParameterValue",
                $"'alert.alertType' must be one of CAUTION, IMPORTANT, NOTE, TIP, WARNING, but got '{word}'."));
        var text = RequireText(b, "text");
        return text.IsSuccess
            ? Result<IMarkdownBlock?>.Success(new AlertBlock(WordToAlert(word), text.Value!))
            : FailIMarkdownBlock(text.Error!);
    }

    private static Result<IMarkdownBlock?> ParseCodeBlock(JsonElement b)
    {
        var code = RequireText(b, "code");
        if (!code.IsSuccess) return FailIMarkdownBlock(code.Error!);
        string? language = null;
        if (b.TryGetProperty("language", out var langEl))
        {
            if (langEl.ValueKind != JsonValueKind.String)
                return FailIMarkdownBlock(new Error("InvalidParameterType", "'codeBlock.language' must be a string."));
            language = langEl.GetString();
        }
        return Result<IMarkdownBlock?>.Success(new CodeBlock(code.Value!, language));
    }

    private static Result<IMarkdownBlock?> ParseSpace(JsonElement b)
    {
        int count = 1;
        if (b.TryGetProperty("count", out var countEl))
        {
            if (countEl.ValueKind != JsonValueKind.Number || !countEl.TryGetInt32(out count))
                return FailIMarkdownBlock(new Error("InvalidParameterType", "'space.count' must be an integer."));
            if (count < 1)
                return FailIMarkdownBlock(new Error("InvalidParameterValue", "'space.count' must be >= 1."));
        }
        return Result<IMarkdownBlock?>.Success(new SpaceBlock(count));
    }

    private static Result<IMarkdownBlock?> ParseList(JsonElement b, string type)
    {
        if (!b.TryGetProperty("items", out var itemsEl))
            return FailIMarkdownBlock(new Error("MissingParameter", $"'{type}' blocks require 'items'."));
        if (itemsEl.ValueKind != JsonValueKind.Array)
            return FailIMarkdownBlock(new Error("InvalidParameterType", $"'{type}.items' must be an array."));
        var items = ParseListItems(itemsEl);
        return items.IsSuccess
            ? Result<IMarkdownBlock?>.Success(new ListBlock(
                type == "numberedList" ? ListKind.Numbered : ListKind.Unordered, items.Value!))
            : FailIMarkdownBlock(items.Error!);
    }

    private static Result<IReadOnlyList<ListItem>> ParseListItems(JsonElement arr) =>
        ParseListItems(arr, depth: 0);

    private static Result<IReadOnlyList<ListItem>> ParseListItems(JsonElement arr, int depth)
    {
        if (depth > 16)
            return FailList(new Error("InvalidParameterValue", "list nesting exceeds the maximum depth of 16."));

        var items = new List<ListItem>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                return FailList(new Error("InvalidParameterType", $"each list item must be an object, but got {el.ValueKind}."));
            if (!el.TryGetProperty("text", out var textEl))
                return FailList(new Error("MissingParameter", "each list item requires 'text'."));
            if (textEl.ValueKind != JsonValueKind.String)
                return FailList(new Error("InvalidParameterType", $"list item 'text' must be a string, but got {textEl.ValueKind}."));
            var text = textEl.GetString()!;
            if (text.Length == 0)
                return FailList(new Error("InvalidParameterValue", "list item 'text' must not be empty."));

            IReadOnlyList<ListItem>? children = null;
            if (el.TryGetProperty("children", out var childrenEl))
            {
                if (childrenEl.ValueKind != JsonValueKind.Array)
                    return FailList(new Error("InvalidParameterType", "list item 'children' must be an array."));
                var kids = ParseListItems(childrenEl, depth + 1);
                if (!kids.IsSuccess) return FailList(kids.Error!);
                children = kids.Value;
            }
            items.Add(new ListItem(text, children));
        }
        return Result<IReadOnlyList<ListItem>>.Success(items);
    }

    private static Result<IMarkdownBlock?> ParseTaskList(JsonElement b)
    {
        if (!b.TryGetProperty("items", out var itemsEl))
            return FailIMarkdownBlock(new Error("MissingParameter", "'taskList' blocks require 'items'."));
        if (itemsEl.ValueKind != JsonValueKind.Array)
            return FailIMarkdownBlock(new Error("InvalidParameterType", "'taskList.items' must be an array."));
        var items = new List<TaskListItem>();
        foreach (var el in itemsEl.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                return FailIMarkdownBlock(new Error("InvalidParameterType", $"each taskList item must be an object, but got {el.ValueKind}."));
            if (!el.TryGetProperty("label", out var labelEl) || labelEl.ValueKind != JsonValueKind.String)
                return FailIMarkdownBlock(new Error("MissingParameter", "each taskList item requires a string 'label'."));
            if (!el.TryGetProperty("isComplete", out var doneEl) || doneEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return FailIMarkdownBlock(new Error("MissingParameter", "each taskList item requires boolean 'isComplete'."));
            items.Add(new TaskListItem(doneEl.GetBoolean(), labelEl.GetString()!));
        }
        return Result<IMarkdownBlock?>.Success(new TaskListBlock(items));
    }

    private static Result<IMarkdownBlock?> ParseTable(JsonElement b)
    {
        if (!b.TryGetProperty("headers", out var headersEl) || headersEl.ValueKind != JsonValueKind.Array)
            return FailIMarkdownBlock(new Error("MissingParameter", "'table' blocks require a non-empty 'headers' array."));
        if (!headersEl.EnumerateArray().Any())
            return FailIMarkdownBlock(new Error("InvalidParameterValue", "'table.headers' must have at least one column."));
        if (!b.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
            return FailIMarkdownBlock(new Error("InvalidParameterType", "'table.rows' must be an array of arrays."));

        var headers = new List<TableHeader>();
        foreach (var h in headersEl.EnumerateArray())
        {
            if (h.ValueKind == JsonValueKind.String)
            {
                headers.Add(new TableHeader(h.GetString()!));
                continue;
            }
            if (h.ValueKind != JsonValueKind.Object)
                return FailIMarkdownBlock(new Error("InvalidParameterType",
                    "each table header must be a string or an object with 'text' (+optional 'align')."));
            if (!h.TryGetProperty("text", out var ht) || ht.ValueKind != JsonValueKind.String)
                return FailIMarkdownBlock(new Error("MissingParameter", "object table headers require a string 'text'."));
            TableAlign? align = null;
            if (h.TryGetProperty("align", out var alignEl))
            {
                if (alignEl.ValueKind != JsonValueKind.String)
                    return FailIMarkdownBlock(new Error("InvalidParameterType", "'align' must be \"left\", \"center\", or \"right\"."));
                align = alignEl.GetString() switch
                {
                    "left" => TableAlign.Left,
                    "center" => TableAlign.Center,
                    "right" => TableAlign.Right,
                    var other => (TableAlign?)null,
                };
                if (align is null)
                    return FailIMarkdownBlock(new Error("InvalidParameterValue", "'align' must be \"left\", \"center\", or \"right\"."));
            }
            headers.Add(new TableHeader(ht.GetString()!, align));
        }

        var headerCount = headers.Count;
        var rows = new List<IReadOnlyList<string>>();
        foreach (var rowEl in rowsEl.EnumerateArray())
        {
            if (rowEl.ValueKind != JsonValueKind.Array)
                return FailIMarkdownBlock(new Error("InvalidParameterType", "each table row must be an array of strings."));
            var cells = new List<string>();
            foreach (var c in rowEl.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.String)
                    return FailIMarkdownBlock(new Error("InvalidParameterType", "each table cell must be a string."));
                cells.Add(c.GetString()!);
            }
            if (cells.Count != headerCount)
                return FailIMarkdownBlock(new Error("InvalidParameterValue",
                    $"Table row cell count ({cells.Count}) does not match header count ({headerCount})."));
            rows.Add(cells);
        }
        return Result<IMarkdownBlock?>.Success(new TableBlock(headers, rows));
    }

    private static Result<string> RequireText(JsonElement b, string field)
    {
        if (!b.TryGetProperty(field, out var el))
            return Result<string>.Failure(new Error("MissingParameter", $"'{field}' is required."));
        if (el.ValueKind != JsonValueKind.String)
            return Result<string>.Failure(new Error("InvalidParameterType", $"'{field}' must be a string, but got {el.ValueKind}."));
        return Result<string>.Success(el.GetString()!);
    }

    private static HashSet<string>? TypeFields(string type) => type switch
    {
        "text" => new HashSet<string>(StringComparer.Ordinal) { "text" },
        "header" => new HashSet<string>(StringComparer.Ordinal) { "level", "text" },
        "quote" => new HashSet<string>(StringComparer.Ordinal) { "text" },
        "alert" => new HashSet<string>(StringComparer.Ordinal) { "alertType", "text" },
        "codeBlock" => new HashSet<string>(StringComparer.Ordinal) { "language", "code" },
        "space" => new HashSet<string>(StringComparer.Ordinal) { "count" },
        "unorderedList" or "numberedList" => new HashSet<string>(StringComparer.Ordinal) { "items" },
        "taskList" => new HashSet<string>(StringComparer.Ordinal) { "items" },
        "table" => new HashSet<string>(StringComparer.Ordinal) { "headers", "rows" },
        _ => null,
    };

    private static Result<IMarkdownBlock?> FailIMarkdownBlock(Error e) => Result<IMarkdownBlock?>.Failure(e);
    private static Result<IReadOnlyList<ListItem>> FailList(Error e) => Result<IReadOnlyList<ListItem>>.Failure(e);
}
