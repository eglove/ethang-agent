using System.Collections;
using System.Management.Automation;
using System.Text.Json;

namespace eThangAgent.PowerShell.ACL;

/// <summary>
///     Converts live PowerShell values to tool-input JSON. Conversion happens here —
///     never ConvertTo-Json — so depth, key handling, and rejection of non-JSON values
///     are deterministic and error messages carry the value's path.
/// </summary>
public static class PowerShellValueConverter
{
    private const int MaxDepth = 32;

    public static string ToJson(object? value)
    {
        var json = Serialize(value, "$", 0);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetRawText();
    }

    private static string Serialize(object? value, string path, int depth)
    {
        if (depth > MaxDepth)
            throw new ExecInputConversionException(
                $"'{path}': value nesting exceeds {MaxDepth} levels.");

        return value switch
        {
            null => "null",
            string s => JsonSerializer.Serialize(s),
            bool b => b ? "true" : "false",
            char c => JsonSerializer.Serialize(c.ToString()),
            sbyte or byte or short or ushort or int or uint or long or ulong
                => value.ToString()!,
            float or double or decimal => JsonSerializer.Serialize(Convert.ToDouble(value)),
            PSObject pso when pso.BaseObject is { } inner
                && !ReferenceEquals(inner, pso)
                && inner is not PSCustomObject
                => Serialize(inner, path, depth + 1),
            _ => SerializeComplex(value, path, depth),
        };
    }

    private static string SerializeComplex(object value, string path, int depth)
    {
        if (value is IDictionary dict)
        {
            var pairs = new List<string>();
            foreach (DictionaryEntry entry in dict)
            {
                var key = entry.Key?.ToString()
                    ?? throw new ExecInputConversionException(
                        $"'{path}': dictionary keys must be strings or numbers.");
                pairs.Add($"{JsonSerializer.Serialize(key)}:" +
                          Serialize(entry.Value, $"{path}.{key}", depth + 1));
            }
            return "{" + string.Join(",", pairs) + "}";
        }

        if (value is IEnumerable sequence)
        {
            var items = new List<string>();
            var index = 0;
            foreach (var item in sequence)
                items.Add(Serialize(item, $"{path}[{index++}]", depth + 1));
            return "[" + string.Join(",", items) + "]";
        }

        if (value is PSObject pso && pso.Properties.Any(p => p.IsInstance))
        {
            var pairs = new List<string>();
            foreach (var property in pso.Properties)
            {
                if (!property.IsInstance) continue;
                pairs.Add($"{JsonSerializer.Serialize(property.Name)}:" +
                          Serialize(property.Value, $"{path}.{property.Name}", depth + 1));
            }
            return "{" + string.Join(",", pairs) + "}";
        }

        throw new ExecInputConversionException(
            $"'{path}': value of type '{value.GetType().Name}' cannot be converted to tool " +
            "input JSON. Use strings, numbers, booleans, hashtables, and arrays only.");
    }
}
