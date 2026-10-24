using Avalonia.Data.Converters;
using Avalonia.Media;

namespace eThangAgent.Desktop.Views;

/// <summary>Converters for the workbench strip (issue #8). Small and local to the view:
///     they carry presentation decisions (which icon looks active, how unavailable
///     reads) that no view-model should own.</summary>
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
}
