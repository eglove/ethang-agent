using System.Collections.Concurrent;
using System.Diagnostics;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace eThangAgent.CLI.Tests;

public class FastAnsiDriverTests
{
    [Fact]
    public void Register_MakesFastAnsiDriverAvailable()
    {
        CliDriver.Register();

        Assert.True(DriverRegistry.TryGetDriver(CliDriver.Name, out var descriptor));
        Assert.NotNull(descriptor);
        Assert.IsType<FastAnsiComponentFactory>(descriptor.CreateFactory());
    }

    [Fact]
    public void Factory_CreateInput_ReturnsFastAnsiInput()
    {
        var factory = new FastAnsiComponentFactory();

        Assert.IsType<FastAnsiInput>(factory.CreateInput());
    }

    [Fact]
    public void ApplyPerformanceSettings_RaisesMainLoopIterationRate()
    {
        var original = Application.MaximumIterationsPerSecond;
        try
        {
            CliDriver.ApplyPerformanceSettings();

            Assert.True(Application.MaximumIterationsPerSecond >= 250);
        }
        finally
        {
            Application.MaximumIterationsPerSecond = original;
        }
    }

    [Fact]
    public void AttachComponentFactory_InjectsFastAnsiComponentFactoryIntoApplication()
    {
        // Full driver boot is not exercised here: initializing Terminal.Gui's real
        // pipeline inside a test host hangs intermittently. Boot behavior is verified
        // by running the CLI (docs/upstream notes cover the seam's fragility).
        using var app = Terminal.Gui.App.Application.Create();
        var before = FastAnsiComponentFactory.InstancesCreated;

        CliDriver.AttachComponentFactory(app);

        Assert.True(FastAnsiComponentFactory.InstancesCreated > before,
            "AttachComponentFactory did not create a FastAnsiComponentFactory");
        var field = FindInstanceField(app.GetType(), "_componentFactory");
        Assert.NotNull(field);
        Assert.IsType<FastAnsiComponentFactory>(field!.GetValue(app));
    }

    private static System.Reflection.FieldInfo? FindInstanceField(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field is not null)
                return field;
        }
        return null;
    }

    [Fact]
    public async Task FastAnsiInput_DeliversInjectedInput_WithLowLatency()
    {
        var input = new FastAnsiInput();
        var queue = new ConcurrentQueue<char>();
        input.Initialize(queue);

        using var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => input.Run(cts.Token));
        try
        {
            var testable = (ITestableInput<char>)input;
            for (var i = 0; i < 30; i++)
            {
                var expected = (char)('a' + i % 26);
                testable.InjectInput(expected);

                var sw = Stopwatch.StartNew();
                while (queue.Count <= i && sw.ElapsedMilliseconds < 1000)
                    Thread.Sleep(0);

                Assert.True(queue.Count > i, $"input {i} ('{expected}') not delivered within 1000ms");
                Assert.True(sw.ElapsedMilliseconds < 15,
                    $"input {i} took {sw.ElapsedMilliseconds}ms — input loop appears throttled");
            }
        }
        finally
        {
            cts.Cancel();
        }
        await Task.WhenAny(runTask, Task.Delay(2000));
        Assert.True(runTask.IsCompleted, "FastAnsiInput.Run did not stop after cancellation");
    }
}
