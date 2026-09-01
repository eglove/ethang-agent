using eThangAgent.SharedKernel;
namespace eThangAgent.AgentDomain;

/// <summary>Linked-agent registry (step 10, D10/P7): the ONLY route to agents outside the
///     local tree. Links are named, explicitly consented, and revocable — isolation by
///     default is a permanent property. Cross-container/cross-workspace contact without a
///     link fails NotLinked at validation.</summary>
public sealed class AgentLinkRegistry
{
  private readonly Lock _gate = new();
  private readonly Dictionary<string, AgentLink> _links = [];

  /// <summary>Registers (or replaces) a consented link. The consent flag is caller-asserted:
  ///     the host only surfaces link creation after its own user consent flow.</summary>
  public Result<LinkAddress> Link(string name, string container, string agentAddress, bool consented)
  {
    if (string.IsNullOrWhiteSpace(name))
    {
      return Result.Failure<LinkAddress>(new DomainError("InvalidLink", "link name must not be empty."));
    }

    if (!consented)
    {
      return Result.Failure<LinkAddress>(new DomainError("ConsentRequired",
          "a link without explicit consent is never created (D10)."));
    }

    lock (_gate)
    {
      AgentLink link = new(name, container, agentAddress, DateTimeOffset.UtcNow);
      _links[name] = link;
      return Result.Success(new LinkAddress(link.Name, link.Container, link.AgentAddress));
    }
  }

  /// <summary>Revokes a named link. Unknown names are a NotFound failure — revocation of a
  ///     link that never existed is information, not silence (A3).</summary>
  public Result<bool> Revoke(string name)
  {
    lock (_gate)
    {
      return _links.Remove(name)
          ? Result.Success(true)
          : Result.Failure<bool>(new DomainError("NotFound", $"no link named '{name}'."));
    }
  }

  /// <summary>Resolves a name to an address, or fails NotLinked (the error contract of the
  ///     source spec's Section 12).</summary>
  public Result<LinkAddress> Resolve(string name)
  {
    lock (_gate)
    {
      return _links.TryGetValue(name, out AgentLink? link)
          ? Result.Success(new LinkAddress(link.Name, link.Container, link.AgentAddress))
          : Result.Failure<LinkAddress>(new DomainError("NotLinked",
              $"no linked agent named '{name}'; contact requires an explicit, consented link."));
    }
  }

  private sealed record AgentLink(string Name, string Container, string AgentAddress, DateTimeOffset LinkedAt);
}

public sealed record LinkAddress(string Name, string Container, string AgentAddress);
