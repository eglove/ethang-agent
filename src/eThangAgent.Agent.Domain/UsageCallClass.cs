using System.Text.Json;
using System.Text.Json.Serialization;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>What kind of provider call a usage row records (issue #22): the closed set
/// <c>main_loop</c> | <c>compaction</c>.
///     <para>There is no third member: automatic model selection no longer exists (the
///     selector was deleted with the smart-selection feature), so a <c>model_selection</c>
///     class would name a call site that cannot occur.</para>
///     <para>A NULL class reads as <see cref="MainLoop"/> — every row written before the
///     class existed came from the loop, so null has exactly one correct meaning. That
///     rule is applied at the parse boundary (<see cref="FromWire"/>), which is where a
///     legacy payload's absent member arrives.</para>
///     <para>Strictness: the type cannot hold an unknown class, and
///     <see cref="FromWire"/> answers an unknown one with a domain error rather than
///     coercing it — a rejected record is a DROPPED record, which the recording site
///     counts and surfaces (issue #23) instead of swallowing.</para></summary>
[JsonConverter(typeof(Converter))]
public sealed record UsageCallClass
{
  /// <summary>The wire name of a main-loop provider call: the class of every row written
  ///     before classes existed, and of every ordinary iteration.</summary>
  public const string MainLoopWire = "main_loop";

  /// <summary>The wire name of a compaction summarizer call.</summary>
  public const string CompactionWire = "compaction";

  /// <summary>An ordinary agent-loop provider call.</summary>
  public static UsageCallClass MainLoop { get; } = new(MainLoopWire);

  /// <summary>A compaction summarizer call, made by the compactor on the loop's behalf.</summary>
  public static UsageCallClass Compaction { get; } = new(CompactionWire);

  private UsageCallClass(string wireName) => WireName = wireName;

  /// <summary>The class's persisted name (<c>main_loop</c> or <c>compaction</c>).</summary>
  public string WireName { get; }

  /// <summary>Resolves a persisted class name: an absent name (a row written before the
  ///     class existed) reads as <see cref="MainLoop"/>, and anything outside the closed
  ///     set is a named domain error — never silently coerced to a valid class.</summary>
  public static Result<UsageCallClass> FromWire(string? wireName) => wireName switch
  {
    null or MainLoopWire => Result.Success(MainLoop),
    CompactionWire => Result.Success(Compaction),
    _ => Result.Failure<UsageCallClass>(new DomainError("InvalidCallClass",
        $"Unknown usage call class '{wireName}'; expected one of: {MainLoopWire}, {CompactionWire}.")),
  };

  public override string ToString() => WireName;

  /// <summary>Reads and writes the class as its bare wire name. An unknown persisted name
  ///     is an infrastructure fault (corrupt stored data), never a silent coercion.</summary>
  // Named decision (CA1034): the converter nests inside the type it serializes by pinned
  // design — one converter travels with its wire shape.
#pragma warning disable CA1034 // Do not nest type
  public sealed class Converter : JsonConverter<UsageCallClass>
  {
    public override UsageCallClass Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
      string? wireName = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
      Result<UsageCallClass> parsed = FromWire(wireName);
      return parsed.IsSuccess
          ? parsed.Value
          : throw new JsonException(parsed.Error.Message);
    }

    public override void Write(Utf8JsonWriter writer, UsageCallClass value, JsonSerializerOptions options)
    {
      ArgumentNullException.ThrowIfNull(writer);
      ArgumentNullException.ThrowIfNull(value);
      writer.WriteStringValue(value.WireName);
    }
  }
#pragma warning restore CA1034 // Do not nest type
}
