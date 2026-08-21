# Keystroke latency: input-thread poll (20ms) + main-loop iteration budget stack to ~30-60ms per key

## Summary

In Terminal.Gui 2.4.17, every keystroke in a TUI incurs **~30-60ms of added latency** from two independent throttles. On a chat-style input this is clearly perceptible ("text lags behind keystrokes"). Both delays are hardcoded/configurable-only-by-side-effect, and both are measured below.

## Environment

- Terminal.Gui 2.4.17
- Windows 11, Windows Terminal, .NET 10
- Default driver (`ansi`), also reproducible with `windows` driver (same input loop)

## Delay 1: input-thread poll — `InputImpl<T>.Run`

`Terminal.Gui/Drivers/Input/InputImpl.cs`:

```csharp
while (true)
{
    if (Peek()) { /* drain into InputQueue */ continue; }
    cancellationToken.ThrowIfCancellationRequested();
    Task.Delay(20, cancellationToken).Wait(cancellationToken);   // ← hardcoded
}
```

A keystroke sits in the console buffer for **0-20ms (avg ~10ms)** before the input thread's next poll picks it up. Fast typing is batched into 2-3-char clumps per poll. This applies to all drivers (`AnsiInput`, `NetInput`, `WindowsInput` share this base loop).

## Delay 2: main-loop iteration budget — `ApplicationMainLoop<T>.Iteration`

```csharp
public void Iteration()
{
    ...
    int num = 1000 / Math.Max(1, (int)Application.MaximumIterationsPerSecond);
    IterationImpl();                       // ← queued input is processed HERE, once per iteration
    TimeSpan timeSpan = DateTime.Now - now;
    TimeSpan delay = TimeSpan.FromMilliseconds(num) - timeSpan;
    if (delay.Milliseconds > 0)
        Task.Delay(delay).Wait();          // ← sleeps the remainder of the budget
}
```

`Application.MaximumIterationsPerSecond` defaults to **25**, i.e. a 40ms budget (`1000/25`). Queued keystrokes wait for the next iteration: **0-40ms more (avg ~20ms)**. The XML docs say "Defaults to 25ms", but `1000 / 25` yields a 40ms budget.

## Combined effect (measured)

| Stage | Avg | Worst |
| --- | --- | --- |
| Input-thread poll | ~10ms | 20ms |
| Main-loop budget | ~20ms | 40ms |
| **Total added keystroke latency** | **~30ms** | **~60ms** |

Measured by injecting keys through the real pipeline and timing key→view-update; app-level key handling itself is microseconds (verified by benchmarking `TextField.NewKeyDownEvent` directly: ~50µs steady-state).

## Partial workaround (what we ship locally)

- `Application.MaximumIterationsPerSecond = 500` (2ms budget) — public API, helps a lot.
- A driver-specific `IInput<T>` subclass re-implementing `Run` with a 1ms poll, registered via `DriverRegistry.Register` and selected with `Init("...")`. Works because the coordinator dispatches `Initialize`/`Run` through the interface, so the re-implementation wins.

This recovers most of the latency but relies on re-implementation seams that may break across releases.

## Suggestions

1. Make the input-thread poll interval configurable (or event-driven: block on a wait handle signaled when the console has input, instead of polling).
2. Decouple input processing from the draw throttle, or lower the default `MaximumIterationsPerSecond` budget impact on input (e.g. drain the input queue on a wait signal rather than once per iteration).
3. Fix the doc/comment mismatch ("Defaults to 25ms" vs actual 40ms budget at the default of 25).

## Related gap: custom drivers registered in DriverRegistry cannot be selected by name

`ApplicationImpl.CreateDriver(string)` (2.4.17) validates the name against `DriverRegistry.TryGetDriver` but then dispatches with a **hardcoded switch** over `"windows"/"dotnet"/"ansi"`, constructing the built-in factories directly and ignoring `DriverDescriptor.CreateFactory`:

```csharp
switch (_driverName)
{
case "windows": Coordinator = CreateSubcomponents(() => new WindowsComponentFactory()); break;
case "dotnet":  Coordinator = CreateSubcomponents(() => new NetComponentFactory()); break;
case "ansi":    Coordinator = CreateSubcomponents(() => new AnsiComponentFactory()); break;
default: throw new InvalidOperationException("Unknown driver name: " + _driverName);
}
```

So `DriverRegistry.Register(...)` + `Init("mydriver")` passes validation and then throws `InvalidOperationException: Unknown driver name: mydriver` (observed on 2.4.17). The registry's `CreateFactory` is dead code on this path. Either dispatch through `descriptor.CreateFactory()` or document that registry registration does not enable name-based selection.

## Related latent bug found while testing

`AnsiInput.Read()` drains `_testInput` and then **unconditionally attempts a console read**. When `ITestableInput<T>.InjectInput` is used in a host that has a console handle in VT mode but no pending console input (e.g. test runners), `Read()` blocks indefinitely and the input thread hangs. `Peek()` returns `true` because of the injected input, so `Run` calls `Read()`. Reproducible on 2.4.17. A guard (only attempt the console read when the console actually has input) would make `InjectInput` safe in mixed environments.
