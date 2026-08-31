using System.Diagnostics;
using eThangAgent.AgentDomain;

namespace eThangAgent.AgentInfrastructure;

/// <summary>In-process event fan-out: synchronous, in subscription order, at-most-once,
///     ephemeral (D1 — no replay, no queue). A throwing subscriber is contained here and
///     logged through the optional sink; it can never break the publishing child loop.
///     Thread-safe.</summary>
public sealed class InProcessAgentEvents(Action<string>? faultLog = null) : IAgentEvents
{
  private readonly Lock _gate = new();
  private readonly List<IAgentEventSubscriber> _subscribers = [];
  private readonly Action<string>? _faultLog = faultLog;

  public IDisposable Subscribe(IAgentEventSubscriber subscriber)
  {
    ArgumentNullException.ThrowIfNull(subscriber);
    lock (_gate)
    {
      _subscribers.Add(subscriber);
    }

    return new Lease(this, subscriber);
  }

  public void Publish(ChildEvent evt)
  {
    ArgumentNullException.ThrowIfNull(evt);
    IAgentEventSubscriber[] snapshot;
    lock (_gate)
    {
      snapshot = [.. _subscribers];
    }

    foreach (IAgentEventSubscriber subscriber in snapshot)
    {
      // Named decision (CA1031): the stream is a fault boundary — one subscriber's
      // failure must never reach the child loop nor the other subscribers.
      try
      {
        subscriber.OnEvent(evt);
      }
#pragma warning disable CA1031 // Do not catch general exception types
      catch (Exception ex)
      {
        _faultLog?.Invoke($"child-event subscriber fault: {ex.Message}");
        Debug.WriteLineIf(_faultLog is null, $"child-event subscriber fault: {ex}");
      }
#pragma warning restore CA1031 // Do not catch general exception types
    }
  }

  private sealed class Lease(InProcessAgentEvents owner, IAgentEventSubscriber subscriber) : IDisposable
  {
    public void Dispose()
    {
      lock (owner._gate)
      {
        _ = owner._subscribers.Remove(subscriber);
      }
    }
  }
}
