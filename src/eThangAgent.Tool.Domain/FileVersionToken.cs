using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Model-visible token for a <see cref="FileVersion"/>: "v&lt;length&gt;:&lt;ticks&gt;".
///     Read surfaces it; edit/comments/revisions require it back; garbage refuses
///     InvalidParameterValue (strict, never coerced). The token is the model's copy of
///     the freshness pair — the stale-edit guard compares it against the file's
///     current version at the write moment.</summary>
public static class FileVersionToken
{
  /// <summary>Encodes the pair as "v&lt;length&gt;:&lt;ticks&gt;" (invariant culture, no padding).</summary>
  public static string Encode(FileVersion version)
  {
    ArgumentNullException.ThrowIfNull(version);
    return $"v{version.Length}:{version.LastWriteTimeUtc.Ticks}";
  }

  /// <summary>Strict decode: must start with 'v', the remainder splits on ':' into
  ///     exactly 2 parts, both parse as long under the invariant culture, Length must
  ///     be >= 0 and Ticks > 0; anything else is a typed InvalidParameterValue failure
  ///     naming the expected shape.</summary>
  public static Result<FileVersion> Decode(string? token)
  {
    if (string.IsNullOrEmpty(token) || token[0] != 'v')
    {
      return Fail(token);
    }

    string[] parts = token[1..].Split(':');
    if (parts.Length != 2)
    {
      return Fail(token);
    }

    bool lengthOk = long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long length);
    bool ticksOk = long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks);
    return lengthOk && ticksOk && length >= 0 && ticks > 0
      ? Result.Success(new FileVersion(length, new DateTime(ticks, DateTimeKind.Utc)))
      : Fail(token);
  }

  private static Result<FileVersion> Fail(string? token)
  {
    return Result.Failure<FileVersion>(new DomainError(ToolErrorCodes.InvalidParameterValue,
        $"Invalid version token '{token}'. Expected the shape v<length>:<ticks> — the exact token a previous docx read returned."));
  }
}
