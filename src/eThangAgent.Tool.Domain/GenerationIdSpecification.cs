using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Business rule for a generation id (the per-request log key): the
///     'gen-' prefix OpenRouter assigns, 1..128 characters total, no whitespace.
///     A cheap gate that catches swapped arguments before any wire call.</summary>
public static class GenerationIdSpecification
{
  private const int MaxLength = 128;

  /// <summary>Validates the id; success carries it trimmed.</summary>
  public static Result<string> Validate(string id)
  {
    ArgumentNullException.ThrowIfNull(id);
    string trimmed = id.Trim();
    return (trimmed.Length, trimmed.Any(char.IsWhiteSpace), trimmed.StartsWith("gen-", StringComparison.Ordinal)) switch
    {
      (0, _, _) => Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'id' must be a non-empty generation id (gen-...).")),
      (_, true, _) => Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'id' must not contain whitespace.")),
      (_, _, false) => Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'id' must be a generation id starting with 'gen-' (got '{trimmed[..Math.Min(24, trimmed.Length)]}').")),
      (_, _, _) when trimmed.Length > MaxLength => Result.Failure<string>(new DomainError(
          ToolErrorCodes.InvalidParameterValue,
          $"'id' must be at most {MaxLength.ToString(CultureInfo.InvariantCulture)} characters (got {trimmed.Length.ToString(CultureInfo.InvariantCulture)}).")),
      _ => Result.Success(trimmed),
    };
  }
}
