using System.Buffers;
using System.Text;
using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public enum TodoStatus { Pending, InProgress, Completed }

public sealed record TodoItem(int Id, string Description, TodoStatus Status);

/// <summary>Strict codec for the persisted todo document: a JSON array of items
///     with exactly the fields id / description / status. Anything else — unknown
///     fields, non-positive or duplicate ids, empty descriptions, statuses outside
///     the enum — is corrupt and fails closed with StorageCorrupt; the caller never
///     silently resets it.</summary>
public static class TodoDocument
{
  public static IReadOnlyList<TodoItem> Empty { get; } = [];

  public static Result<IReadOnlyList<TodoItem>> Parse(string json)
  {
    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(json);
    }
    catch (JsonException ex)
    {
      return Fail($"value is not valid JSON ({ex.Message}).");
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind != JsonValueKind.Array)
      {
        return Fail($"value must be a JSON array, but got {doc.RootElement.ValueKind}.");
      }

      List<TodoItem> items = [];
      HashSet<int> seenIds = [];
      int index = 0;
      foreach (JsonElement element in doc.RootElement.EnumerateArray())
      {
        index++;
        Result<TodoItem> item = ParseItem(element, index);
        if (!item.IsSuccess)
        {
          return Result.Failure<IReadOnlyList<TodoItem>>(item.Error!);
        }

        if (!seenIds.Add(item.Value!.Id))
        {
          return Fail($"item {index} repeats id {item.Value.Id}; ids must be unique.");
        }

        items.Add(item.Value);
      }

      return Result.Success<IReadOnlyList<TodoItem>>(items);
    }
  }

  public static string Serialize(IReadOnlyList<TodoItem> items)
  {
    ArgumentNullException.ThrowIfNull(items);
    ArrayBufferWriter<byte> buffer = new();
    using Utf8JsonWriter writer = new(buffer);
    writer.WriteStartArray();
    foreach (TodoItem item in items)
    {
      writer.WriteStartObject();
      writer.WriteNumber("id", item.Id);
      writer.WriteString("description", item.Description);
      writer.WriteString("status", StatusText(item.Status));
      writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.Flush();
    return Encoding.UTF8.GetString(buffer.WrittenSpan);
  }

  public static string StatusText(TodoStatus status) => status switch
  {
    TodoStatus.Pending => nameof(TodoStatus.Pending),
    TodoStatus.InProgress => nameof(TodoStatus.InProgress),
    TodoStatus.Completed => nameof(TodoStatus.Completed),
    // Unnamed enum values cannot occur; fall through to the same text.
    _ => nameof(TodoStatus.Completed),
  };

  private static Result<TodoItem> ParseItem(JsonElement element, int index)
  {
    if (element.ValueKind != JsonValueKind.Object)
    {
      return ItemFail(index, $"must be a JSON object, but got {element.ValueKind}.");
    }

    int id = 0;
    string? description = null;
    TodoStatus status = default;
    bool hasId = false;

    foreach (JsonProperty property in element.EnumerateObject())
    {
      switch (property.Name)
      {
        case "id":
          if (property.Value.ValueKind != JsonValueKind.Number ||
              !property.Value.TryGetInt32(out id))
          {
            return ItemFail(index, "'id' must be an integer.");
          }

          hasId = true;
          break;

        case "description":
          if (property.Value.ValueKind != JsonValueKind.String)
          {
            return ItemFail(index,
                $"'description' must be a string, but got {property.Value.ValueKind}.");
          }

          description = property.Value.GetString();
          break;

        case "status":
          if (property.Value.ValueKind != JsonValueKind.String)
          {
            return ItemFail(index,
                $"'status' must be a string, but got {property.Value.ValueKind}.");
          }

          string? statusText = property.Value.GetString();
          if (statusText is not (nameof(TodoStatus.Pending)
              or nameof(TodoStatus.InProgress) or nameof(TodoStatus.Completed)))
          {
            return ItemFail(index,
                $"'status' must be one of Pending, InProgress, Completed, but got '{statusText}'.");
          }

          status = statusText switch
          {
            nameof(TodoStatus.Pending) => TodoStatus.Pending,
            nameof(TodoStatus.InProgress) => TodoStatus.InProgress,
            _ => TodoStatus.Completed,
          };
          break;

        default:
          return ItemFail(index, $"has unknown field '{property.Name}'.");
      }
    }

    if (!hasId)
    {
      return ItemFail(index, "is missing 'id'.");
    }

    if (id <= 0)
    {
      return ItemFail(index, "'id' must be a positive integer.");
    }

    if (description is null)
    {
      return ItemFail(index, "is missing 'description'.");
    }

    if (description.Length == 0)
    {
      return ItemFail(index, "'description' must be a non-empty string.");
    }

    TodoItem item = new(id, description, status);
    return Result.Success(item);
  }

  private static Result<TodoItem> ItemFail(int index, string detail) =>
      Result.Failure<TodoItem>(new DomainError("StorageCorrupt", $"item {index} {detail}"));

  private static Result<IReadOnlyList<TodoItem>> Fail(string detail) =>
      Result.Failure<IReadOnlyList<TodoItem>>(new DomainError("StorageCorrupt",
          $"Stored todo document is invalid: {detail}"));
}
