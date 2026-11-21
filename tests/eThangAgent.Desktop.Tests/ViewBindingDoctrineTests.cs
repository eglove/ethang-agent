using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace eThangAgent.Desktop.Tests;

/// <summary>Every property a view binds must notify when it changes. A plain auto-property
///     bound in XAML renders once and then never again, so the control it feeds is frozen:
///     a form flagged open by one stays invisible, a button guarded by one stays disabled,
///     and no view-model test can see the break because the view-model's own state is
///     correct. The scan walks each view's elements, carries the nearest <c>x:DataType</c>
///     down the tree so a template's bindings are read against the type it declares, and
///     reports any bound property declared there without <c>[ObservableProperty]</c>.
///     Computed properties are exempt: they are re-read when a property they depend on
///     raises, which is the notification rule working as intended.</summary>
public partial class ViewBindingDoctrineTests
{
  [Fact]
  public void EveryPropertyAViewBinds_NotifiesWhenItChanges()
  {
    Dictionary<string, string> sources = ViewModelSources();
    List<string> violations = [];
    foreach (string view in Directory.EnumerateFiles(
        Path.Combine(RepoRoot(), "src", "eThangAgent.Desktop", "Views"), "*.axaml"))
    {
      XDocument document = XDocument.Load(view);
      Walk(document.Root!, null, (typeName, bound) =>
      {
        if (sources.TryGetValue(typeName, out string? source)
            && SilentAutoProperties(File.ReadAllText(source)).Contains(bound))
        {
          violations.Add($"{Path.GetFileName(view)} binds {typeName}.{bound}, which never raises");
        }
      });
    }

    Assert.True(violations.Count == 0,
        "bound properties that never notify (declare them [ObservableProperty] so the view "
        + "re-reads them): " + string.Join("; ", violations.Distinct(StringComparer.Ordinal)));
  }

  /// <summary>Walks an element subtree, carrying the nearest <c>x:DataType</c> declaration
  ///     down it, and reports every bound property with the type it is read against.</summary>
  private static void Walk(XElement element, string? scope, Action<string, string> onBinding)
  {
    string? declared = element.Attributes()
        .Where(a => a.Name.LocalName == "DataType" && a.Name.NamespaceName.Contains("xaml", StringComparison.Ordinal))
        .Select(a => a.Value)
        .FirstOrDefault();
    string? current = declared ?? scope;
    string? typeName = current?.StartsWith("vm:", StringComparison.Ordinal) == true ? current[3..] : null;
    if (typeName is not null)
    {
      foreach (XAttribute attribute in element.Attributes())
      {
        foreach (Match binding in BoundPropertyPattern().Matches(attribute.Value))
        {
          onBinding(typeName, binding.Groups[1].Value);
        }
      }
    }

    foreach (XElement child in element.Elements())
    {
      Walk(child, current, onBinding);
    }
  }

  /// <summary>Every type the desktop's view-models declare, by name, with the file that
  ///     declares it - the lookup the scan needs to read a bound type's properties.</summary>
  private static Dictionary<string, string> ViewModelSources()
  {
    Dictionary<string, string> sources = [with(StringComparer.Ordinal)];
    foreach (string file in Directory.EnumerateFiles(
        Path.Combine(RepoRoot(), "src", "eThangAgent.Desktop", "ViewModels"), "*.cs"))
    {
      foreach (Match declared in TypeDeclarationPattern().Matches(File.ReadAllText(file)))
      {
        sources[declared.Groups[1].Value] = file;
      }
    }

    return sources;
  }

  /// <summary>The auto-properties in a source file that carry no <c>[ObservableProperty]</c>.
  ///     An auto-property is one whose accessors are the compiler's: <c>get; set;</c> or
  ///     <c>get; private set;</c>. A computed property has a body and is not listed.</summary>
  private static HashSet<string> SilentAutoProperties(string source)
  {
    HashSet<string> silent = [with(StringComparer.Ordinal)];
    string[] lines = source.Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
      Match property = AutoPropertyPattern().Match(lines[i]);
      if (property.Success && !IsObservable(lines, i))
      {
        _ = silent.Add(property.Groups[1].Value);
      }
    }

    return silent;
  }

  /// <summary>True when the declaration at the given line is led by an
  ///     <c>[ObservableProperty]</c> attribute. A member's attributes and doc comment are the
  ///     lines directly above it, so the search stops at the first line that is neither.</summary>
  private static bool IsObservable(string[] lines, int index)
  {
    for (int above = index - 1; above >= 0; above--)
    {
      string trimmed = lines[above].Trim();
      if (trimmed.Length == 0)
      {
        continue;
      }

      if (trimmed.StartsWith('[', StringComparison.Ordinal) && trimmed.EndsWith(']', StringComparison.Ordinal))
      {
        if (trimmed.Contains("[ObservableProperty]", StringComparison.Ordinal))
        {
          return true;
        }

        continue;
      }

      if (!trimmed.StartsWith("///", StringComparison.Ordinal))
      {
        break;
      }
    }

    return false;
  }

  private static string RepoRoot()
  {
    DirectoryInfo? dir = new(typeof(ViewBindingDoctrineTests).Assembly.Location);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    return dir!.FullName;
  }

  [GeneratedRegex(@"^\{Binding\s+(?:Path=)?!?([A-Za-z_]\w*)")]
  private static partial Regex BoundPropertyPattern();

  [GeneratedRegex(@"(?:class|record)\s+(\w+)")]
  private static partial Regex TypeDeclarationPattern();

  [GeneratedRegex(@"public\s+(?:partial\s+)?[\w\?<>,\.\[\]]+\s+(\w+)\s*\{\s*get;\s*(?:private\s+)?set;\s*\}")]
  private static partial Regex AutoPropertyPattern();
}
