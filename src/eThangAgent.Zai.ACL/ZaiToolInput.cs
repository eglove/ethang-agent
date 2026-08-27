using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Zai.ACL;

/// <summary>Shared strict-parsing helpers for the z.ai tool argument objects. The
///     per-tool input records own their field rules; this class owns the one rule they
///     all share: unknown parameters are rejected, never ignored.</summary>
internal static class ZaiToolInput
{
  /// <summary>Returns the typed error for any argument name outside <paramref name="allowed"/>,
  ///     or null when every supplied parameter is known.</summary>
  internal static DomainError? RejectUnknown(JsonElement json, string allowedList, params string[] allowed)
  {
    HashSet<string> known = new(allowed.Append(ToolTimeout.ParameterName), StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    return unknown.Count == 0
        ? null
        : new DomainError("UnknownParameter",
            $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: {allowedList}.");
  }
}
