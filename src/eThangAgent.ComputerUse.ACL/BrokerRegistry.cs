using System.Security.Cryptography;
using System.Text;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>Process-lifetime supervisor registry keyed by workspace root (spec 1.3): one
///     broker per workspace, shared by every session in it. GetOrCreate is thread-safe.
///     Pipe names derive from a stable hash of the root so reconnects across sessions land
///     on the same broker pipe.</summary>
public sealed class BrokerRegistry(Func<string> hostPath, Func<string, string> pipeNameFor)
{
  private readonly Lock _gate = new();
  private readonly Dictionary<string, BrokerSupervisor> _supervisors = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Production registry: Host exe path is a sibling of the app base directory;
  ///     pipe name is ethang-computer-use-{workspaceId} from the stable root hash.</summary>
  public BrokerRegistry()
    : this(DefaultHostPath, DefaultPipeNameFor)
  {
  }

  public static string DefaultHostPath() => Path.Combine(AppContext.BaseDirectory, "eThangAgent.ComputerUse.Host.exe");

  public static string DefaultPipeNameFor(string workspaceRoot) => "ethang-computer-use-" + StableWorkspaceId(workspaceRoot);

  /// <summary>The workspace id: a stable SHA-256 hash of the full path, uppercase hex.</summary>
  public static string StableWorkspaceId(string workspaceRoot)
  {
    byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspaceRoot)));
    return Convert.ToHexString(bytes);
  }

  /// <summary>Returns the supervisor for the root, creating it on first call. The same root
  ///     always yields the same instance for the process lifetime.</summary>
  public BrokerSupervisor GetOrCreate(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    _gate.Enter();
    try
    {
      if (_supervisors.TryGetValue(workspaceRoot, out BrokerSupervisor? existing))
      {
        return existing;
      }

      BrokerSupervisor created = new(hostPath(), pipeNameFor(workspaceRoot), StableWorkspaceId(workspaceRoot));
      _supervisors[workspaceRoot] = created;
      return created;
    }
    finally
    {
      _gate.Exit();
    }
  }
}
