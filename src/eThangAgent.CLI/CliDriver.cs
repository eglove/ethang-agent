using System.Reflection;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace eThangAgent.CLI;

/// <summary>Registers the CLI's low-latency driver and main-loop settings.</summary>
public static class CliDriver
{
    public const string Name = "fastansi";

    public static void Register()
    {
        if (!DriverRegistry.IsRegistered(Name))
        {
            DriverRegistry.Register(new DriverRegistry.DriverDescriptor(
                Name,
                "Fast ANSI Driver",
                "ANSI driver with a 1ms input poll for low keystroke latency",
                [PlatformID.Win32NT, PlatformID.Unix, PlatformID.MacOSX],
                () => new FastAnsiComponentFactory()));
        }
    }

    /// <summary>
    ///     Creates and initializes the Terminal.Gui application using the fast driver.
    ///     Terminal.Gui 2.4.17's Init(driverName) only dispatches to its three built-in
    ///     factories via a hardcoded switch (ApplicationImpl.CreateDriver) and ignores
    ///     DriverRegistry descriptors, so a registered custom driver can never be selected
    ///     by name. The _componentFactory branch of CreateDriver does dispatch by factory
    ///     type, so we inject our factory there before Init. Remove this reflection seam
    ///     once upstream exposes a public component-factory injection API.
    /// </summary>
    public static IApplication InitApplication()
    {
        var app = Application.Create();
        AttachComponentFactory(app);
        app.Init();
        return app;
    }

    /// <summary>Injects the fast component factory into the application's private seam (see InitApplication).</summary>
    public static void AttachComponentFactory(IApplication app)
    {
        var field = FindField(app.GetType(), "_componentFactory")
            ?? throw new InvalidOperationException(
                "Terminal.Gui ApplicationImpl._componentFactory not found — the reflection seam moved; update CliDriver.");
        field.SetValue(app, new FastAnsiComponentFactory());
    }

    public static void ApplyPerformanceSettings()
    {
        // Terminal.Gui defaults to 25 iterations/sec (a 40ms budget). The main loop
        // processes queued input once per iteration, so a low rate directly adds
        // keystroke latency. 500/sec gives a 2ms budget.
        Application.MaximumIterationsPerSecond = 500;
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
                return field;
        }
        return null;
    }
}
