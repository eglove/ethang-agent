using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public static class StateKey
{
  public static Result<(string Ns, string Name)> Parse(string key)
  {
    if (string.IsNullOrEmpty(key))
    {
      return Failure;
    }

    int slash = key.IndexOf('/', StringComparison.Ordinal);
    if (slash <= 0 || slash == key.Length - 1 || key.IndexOf('/', slash + 1) >= 0)
    {
      return Failure;
    }

    string ns = key[..slash];
    string name = key[(slash + 1)..];
    return ns.Any(char.IsWhiteSpace) || name.Any(char.IsWhiteSpace) ? Failure : Result.Success<(string Ns, string Name)>((ns, name));
  }

  private static Result<(string Ns, string Name)> Failure
        => Result.Failure<(string Ns, string Name)>(
            new DomainError("InvalidKey", "Key must be 'ns/name' with non-empty whitespace-free segments and a single slash."));
}
