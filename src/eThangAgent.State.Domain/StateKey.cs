using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public static class StateKey
{
    public static Result<(string Ns, string Name)> Parse(string key)
    {
        if (string.IsNullOrEmpty(key))
            return Failure;
        var slash = key.IndexOf('/');
        if (slash <= 0 || slash == key.Length - 1 || key.IndexOf('/', slash + 1) >= 0)
            return Failure;
        var ns = key[..slash];
        var name = key[(slash + 1)..];
        if (ns.Any(char.IsWhiteSpace) || name.Any(char.IsWhiteSpace))
            return Failure;
        return Result<(string Ns, string Name)>.Success((ns, name));
    }

    private static Result<(string Ns, string Name)> Failure
        => Result<(string Ns, string Name)>.Failure(
            new Error("InvalidKey", "Key must be 'ns/name' with non-empty whitespace-free segments and a single slash."));
}
