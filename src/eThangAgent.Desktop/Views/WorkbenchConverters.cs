using Avalonia.Data.Converters;
using Avalonia.Media;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>Converters for the workbench strip (issue #8) and its git view (issue #9).
///     Small and local to the view: they carry presentation decisions (which icon looks
///     active, how unavailable reads, what colour a diff line is) that no view-model
///     should own.</summary>
internal static class WorkbenchConverters
{
  /// <summary>Active icon background: a subtle highlight, transparent otherwise.</summary>
  public static readonly IValueConverter ActiveIconBackground =
      new FuncValueConverter<bool, IBrush>(active =>
          active ? new SolidColorBrush(Color.Parse("#334C8CFF")) : Brushes.Transparent);

  /// <summary>Unavailable icons render dimmed (0.35) so 'not built yet' is visible
  ///     before the click rather than after it.</summary>
  public static readonly IValueConverter AvailabilityOpacity =
      new FuncValueConverter<bool, double>(available => available ? 1.0 : 0.35);

  /// <summary>Non-zero count shows its element; zero hides it. Used for section
  ///     headers that should not appear over an empty list.</summary>
  public static readonly IValueConverter CountToVisibility =
      new FuncValueConverter<int, bool>(count => count > 0);

  /// <summary>A diff line's colour by kind: added green, removed red, hunk header the
  ///     muted blue the mockup uses, context the ordinary foreground.
  ///     <para>Colour is the ONLY cue a unified diff has, so the mapping lives in one
  ///     place rather than being re-derived per template.</para></summary>
  public static readonly IValueConverter DiffLineBrush =
      new FuncValueConverter<DiffLineKind, IBrush>(kind => kind switch
      {
        DiffLineKind.Added => new SolidColorBrush(Color.Parse("#5FD18B")),
        DiffLineKind.Removed => new SolidColorBrush(Color.Parse("#FF6B6B")),
        DiffLineKind.Hunk => new SolidColorBrush(Color.Parse("#6F8FC9")),
        DiffLineKind.Context => new SolidColorBrush(Color.Parse("#B9C2D4")),
        _ => Brushes.Transparent,
      });
}
