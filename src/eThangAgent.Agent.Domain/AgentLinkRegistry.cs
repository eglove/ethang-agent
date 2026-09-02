using eThangAgent.SharedKernel;
namespace eThangAgent.AgentDomain;

/// <summary>Linked-agent registry (step 10, D10/P7): the ONLY route to agents outside the
///     local tree. Links are named, explicitly consented, and revocable — isolation by
///     default is a permanent property. W2 supersedes the R2.3 in-memory ruling: with a
///     store + workspace wired (composition), links are hydrated at construction, written
///     through at consent, and deleted at revocation — consent decisions survive restarts.
///     Without a store (unit tests, minimal hosts) behavior is the legacy in-memory
///     registry, byte-identical. <see cref="Resolve"/> reveals only the address tuple,
///     never consent state or metadata (R2.4).</summary>
public sealed class AgentLinkRegistry
{
  private readonly Lock _gate = new();
  private readonly Dictionary<string, AgentLink> _links = [];
  private readonly ILinkStore? _store;
  private readonly Func<string>? _workspaceId;

  /// <summary>Hydrates the workspace's persisted links (W2). A storage fault here is an
  ///     infrastructure fault — the session must not open with silently missing links —
  ///     so it throws a NAMED error instead of half-constructing the registry.</summary>
  public AgentLinkRegistry(ILinkStore? store = null, Func<string>? workspaceId = null)
  {
    _store = store;
    _workspaceId = workspaceId;
    if (store is null || workspaceId is null)
    {
      return;
    }

    Result<IReadOnlyList<StoredLink>> persisted = store.List(workspaceId());
    if (!persisted.IsSuccess)
    {
      throw new InvalidOperationException(
          $"link store hydration failed: {persisted.Error.Code}: {persisted.Error.Message}");
    }

    foreach (StoredLink link in persisted.Value)
    {
      _links[link.Name] = new AgentLink(link.Name, link.Container, link.AgentAddress, link.LinkedAt);
    }
  }

  /// <summary>Registers (or replaces) a consented link — write-through (W2). The consent
  ///     flag is caller-asserted: the host only surfaces link creation after its own user
  ///     consent flow. A consent failure never touches the store; a store failure rolls the
  ///     memory change back and surfaces as the Result failure.</summary>
  public Result<LinkAddress> Link(string name, string container, string agentAddress, bool consented)
  {
    if (string.IsNullOrWhiteSpace(name))
    {
      return Result.Failure<LinkAddress>(new DomainError("InvalidLink", "link name must not be empty."));
    }

    if (!consented)
    {
      return Result.Failure<LinkAddress>(new DomainError("ConsentRequired", "a link without explicit consent is never created (D10)."));
    }

    AgentLink link = new(name, container, agentAddress, DateTimeOffset.UtcNow);
    AgentLink? previous;
    lock (_gate)
    {
      _ = _links.TryGetValue(name, out previous);
      _links[name] = link;
    }

    if (_store is { } store && _workspaceId is { } workspaceId)
    {
      Result<string> persisted = store.Upsert(workspaceId(),
          new StoredLink(name, container, agentAddress, link.LinkedAt));
      if (!persisted.IsSuccess)
      {
        lock (_gate)
        {
          if (previous is { })
          {
            _links[name] = previous;
          }
          else
          {
            _ = _links.Remove(name);
          }
        }

        return Result.Failure<LinkAddress>(persisted.Error);
      }
    }

    return Result.Success(new LinkAddress(link.Name, link.Container, link.AgentAddress));
  }

  /// <summary>Revokes a named link — the persisted row is deleted first (W2: revocation
  ///     must persist; a row already gone is fine), then memory. A name that exists nowhere
  ///     still surfaces NotFound — revocation of a link that never existed is information,
  ///     not silence (A3).</summary>
  public Result<bool> Revoke(string name)
  {
    lock (_gate)
    {
      if (!_links.ContainsKey(name))
      {
        return Result.Failure<bool>(new DomainError("NotFound", $"no link named '{name}'."));
      }
    }

    if (_store is { } store && _workspaceId is { } workspaceId)
    {
      Result<bool> deleted = store.Delete(workspaceId(), name);
      if (!deleted.IsSuccess)
      {
        return Result.Failure<bool>(deleted.Error);
      }
    }

    lock (_gate)
    {
      _ = _links.Remove(name);
    }

    return Result.Success(true);
  }

  /// <summary>The live links, newest first — the consent dialog's list surface. Same
  ///     visibility as the linker themselves (the host's own UI); <see cref="Resolve"/>
  ///     stays the only agent-facing path and still reveals nothing beyond the address.</summary>
  public IReadOnlyList<LinkAddress> Snapshot
  {
    get
    {
      lock (_gate)
      {
        return [.. _links.Values
            .OrderByDescending(l => l.LinkedAt)
            .Select(l => new LinkAddress(l.Name, l.Container, l.AgentAddress))];
      }
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
