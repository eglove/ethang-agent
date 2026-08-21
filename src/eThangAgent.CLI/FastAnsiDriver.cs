using System.Collections.Concurrent;
using Terminal.Gui.Drivers;

namespace eThangAgent.CLI;

/// <summary>
///     AnsiInput variant whose Run loop polls every 1ms instead of the 20ms hardcoded in
///     Terminal.Gui's InputImpl.Run, which adds up to 20ms of keystroke latency.
///     Initialize and Run re-implement IInput&lt;char&gt;: MainLoopCoordinator invokes the input
///     through that interface, so this class's methods win the dispatch.
/// </summary>
public sealed class FastAnsiInput : AnsiInput, IInput<char>, ITestableInput<char>
{
    private readonly ConcurrentQueue<char> _pending = new();
    private ConcurrentQueue<char>? _queue;

    public new void Initialize(ConcurrentQueue<char> inputQueue)
    {
        _queue = inputQueue;
        base.Initialize(inputQueue);
    }

    /// <summary>
    ///     Injected input is buffered locally: AnsiInput.Read() unconditionally attempts a
    ///     console read after draining its test queue, which blocks when a console handle
    ///     exists but no console input is pending (e.g. tests, redirected hosts).
    /// </summary>
    public new void InjectInput(char input) => _pending.Enqueue(input);

    public override bool Peek() => !_pending.IsEmpty || base.Peek();

    public override IEnumerable<char> Read()
    {
        while (_pending.TryDequeue(out var ch))
            yield return ch;

        // Only touch the console when Peek confirms input is actually available;
        // AnsiInput.Read() can otherwise block on the console indefinitely.
        if (base.Peek())
        {
            foreach (var ch in base.Read())
                yield return ch;
        }
    }

    public new void Run(CancellationToken runCancellationToken)
    {
        if (_queue is null)
            throw new InvalidOperationException("Cannot run input before Initialize");

        var linked = ExternalCancellationTokenSource is { } external
            ? CancellationTokenSource.CreateLinkedTokenSource(runCancellationToken, external.Token)
            : null;
        var cancellationToken = linked?.Token ?? runCancellationToken;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var drainedAny = false;
                while (Peek())
                {
                    drainedAny = true;
                    foreach (var record in Read())
                        _queue.Enqueue(record);
                }

                // 1ms poll: same drain semantics as InputImpl.Run, 20x tighter latency.
                if (!drainedAny)
                    Thread.Sleep(1);
            }
        }
        finally
        {
            linked?.Dispose();
        }
    }
}

/// <summary>Component factory that supplies <see cref="FastAnsiInput"/> in place of AnsiInput.</summary>
public sealed class FastAnsiComponentFactory : AnsiComponentFactory
{
    /// <summary>Count of factory instances created; lets tests verify this factory was actually used.</summary>
    public static int InstancesCreated { get; private set; }

    public FastAnsiComponentFactory()
    {
        InstancesCreated++;
    }

    public override IInput<char> CreateInput() => new FastAnsiInput();
}
