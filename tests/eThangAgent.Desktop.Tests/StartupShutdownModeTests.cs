
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;

namespace eThangAgent.Desktop.Tests;

/// <summary>Covers the startup shutdown-mode seams on <see cref="eThangAgent.Desktop.DesktopHost"/>.
///
/// Background: Avalonia's default OnLastWindowClose mode killed the app mid-startup — between
/// framework initialization and the main window being shown, the ONLY windows are transient
/// helpers (folder-picker host, dialogs), so closing one shut the process down the instant a
/// workspace was picked or cancelled.
///
/// The full window-close-to-shutdown causal chain cannot run under Avalonia.Headless.XUnit:
/// headless provides no classic-desktop lifetime, and windows shown against a standalone
/// ClassicDesktopStyleApplicationLifetime never register with it (verified: lifetime.Windows
/// stays empty through Show + MainWindow assignment), so no shutdown request can fire either
/// way. The seams are therefore tested at the contract level here; the end-to-end behavior is
/// covered by launching the real app.</summary>
public sealed class StartupShutdownModeTests
{
    [AvaloniaFact]
    public void Deferral_Switches_Lifetime_To_Explicit_Shutdown()
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime();

        // Documents the platform default that caused the startup race.
        Assert.Equal(ShutdownMode.OnLastWindowClose, lifetime.ShutdownMode);

        eThangAgent.Desktop.DesktopHost.DeferShutdownDuringStartup(lifetime);
        Assert.Equal(ShutdownMode.OnExplicitShutdown, lifetime.ShutdownMode);

        eThangAgent.Desktop.DesktopHost.EnableWindowCloseShutdown(lifetime);
        Assert.Equal(ShutdownMode.OnLastWindowClose, lifetime.ShutdownMode);
    }
}
