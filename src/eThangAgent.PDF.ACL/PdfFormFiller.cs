using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.Advanced;

namespace eThangAgent.PDF.ACL;

/// <summary>The form write path over PDFsharp: fills an existing field and flattens the
///     form, both against the working document a <see cref="PdfManipulator"/> batch holds
///     open. A refused fill returns before the batch is saved, so no operation of the
///     batch reaches disk.
///     <para>Filling writes the field's value and its widget appearance state, then marks
///     the form as needing appearance regeneration: the document states its own values,
///     and a viewer that honours /NeedAppearances redraws them. Flattening merges each
///     field's appearance into the page content and removes the interactive fields, so the
///     result renders identically everywhere and can no longer be edited.</para>
///     </summary>
public static class PdfFormFiller
{
  /// <summary>Fills one field, or a typed <see cref="DomainError"/> with nothing
  ///     changed. The field is addressed by its fully qualified name; the value's meaning
  ///     follows the field's type.</summary>
  public static Result<bool> Fill(PdfDocument doc, FillFormField fill)
  {
    ArgumentNullException.ThrowIfNull(doc);
    ArgumentNullException.ThrowIfNull(fill);

    PdfAcroField? field = FindField(doc, fill.Field);
    if (field is null)
    {
      return Fail(PdfErrorCodes.FormFieldNotFound,
          $"no form field is named '{fill.Field}'; the document's fields are {FieldNames(doc)}.");
    }

    Result<bool> applied = Apply(field, fill.Value);
    if (applied.IsSuccess)
    {
      MarkNeedsAppearances(doc);
    }

    return applied;
  }

  /// <summary>Flattens every field of the form: each field's current value is drawn
  ///     into the page content at its widget rectangle, the widgets are removed from the
  ///     pages, and the form is removed from the catalog. The result renders the values
  ///     without depending on appearance streams and can no longer be edited. A document
  ///     with no form is already flat, so it succeeds unchanged.</summary>
  public static Result<bool> Flatten(PdfDocument doc)
  {
    ArgumentNullException.ThrowIfNull(doc);
    if (TryGetForm(doc) is null)
    {
      return Result.Success(true);
    }

    try
    {
      foreach (PdfPage page in doc.Pages)
      {
        FlattenPage(page);
      }

      _ = doc.Internals.Catalog.Elements.Remove("/AcroForm");
      return Result.Success(true);
    }
#pragma warning disable CA1031 // Named decision: the flatten boundary maps every library fault to a typed DomainError; an unknown failure must never escape as an exception.
    catch (Exception ex)
    {
      return Fail(PdfErrorCodes.CorruptPdf, $"the form could not be flattened: {Clamp(ex.Message)}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Draws every widget on one page into the page content and drops it from
  ///     the annotation list. A widget whose value is empty leaves no mark.</summary>
  private static void FlattenPage(PdfPage page)
  {
    if (page.Elements["/Annots"] is not PdfArray annots)
    {
      return;
    }

    List<PdfItem> remaining = [];
    List<(PdfDictionary Widget, string Text)> drawn = [];
    foreach (PdfItem item in annots.Elements)
    {
      // An annotation is normally an indirect object, so the array entry is a reference
      // rather than the dictionary itself.
      if (Resolve(item) is { } widget && IsWidget(widget))
      {
        string text = DisplayValue(widget);
        if (text.Length > 0)
        {
          drawn.Add((widget, text));
        }
      }
      else
      {
        remaining.Add(item);
      }
    }

    if (drawn.Count == 0 && remaining.Count == annots.Elements.Count)
    {
      return;
    }

    DrawValues(page, drawn);
    if (remaining.Count == 0)
    {
      _ = page.Elements.Remove("/Annots");
    }
    else
    {
      page.Elements["/Annots"] = new PdfArray(page.Owner, [.. remaining]);
    }
  }

  /// <summary>Draws each value into the page content, centred vertically in its widget
  ///     rectangle with a small inset.</summary>
  private static void DrawValues(PdfPage page, List<(PdfDictionary Widget, string Text)> drawn)
  {
    using XGraphics gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
    foreach ((PdfDictionary widget, string text) in drawn)
    {
      if (!widget.Elements.ContainsKey("/Rect"))
      {
        continue;
      }

      PdfRectangle rect = widget.Elements.GetRectangle("/Rect");
      double height = Math.Abs(rect.Height);
      double size = Math.Clamp(height * 0.6, 4, 12);
      double left = Math.Min(rect.X1, rect.X2) + 2;
      double bottom = Math.Min(rect.Y1, rect.Y2) + ((height - size) / 2);
      gfx.DrawString(text, new XFont("Arial", size), XBrushes.Black, new XPoint(left, page.Height.Point - bottom));
    }
  }

  /// <summary>The text a widget contributes to the flattened page: its field's value,
  ///     with a checked box drawn as a cross.</summary>
  private static string DisplayValue(PdfDictionary widget)
  {
    string? name = widget.Elements.GetString("/T");
    string? value = widget.Elements.GetString("/V");
    if (name is null && widget.Elements["/Parent"] is PdfDictionary parent)
    {
      name = parent.Elements.GetString("/T");
      value ??= parent.Elements.GetString("/V");
    }

    _ = name;
    return value is null || string.Equals(value, "/Off", StringComparison.Ordinal)
        ? string.Empty
        : ToggledValue(value);
  }

  /// <summary>A toggle's export value renders as a cross; a text or choice value renders
  ///     as itself.</summary>
  private static string ToggledValue(string value)
      => value.Length > 0 && value[0] == '/' ? "X" : value;

  /// <summary>The dictionary an item names, following one reference indirection.
  ///     </summary>
  private static PdfDictionary? Resolve(PdfItem item) => item switch
  {
    PdfDictionary dictionary => dictionary,
    PdfReference reference => reference.Value as PdfDictionary,
    _ => null,
  };

  /// <summary>True when a dictionary is a widget annotation.</summary>
  private static bool IsWidget(PdfDictionary dictionary) => IsWidgetElements(dictionary.Elements);

  /// <summary>True when a dictionary's elements declare a widget annotation.</summary>
  private static bool IsWidgetElements(PdfDictionary.DictionaryElements elements)
      => elements.ContainsKey("/Subtype")
         && string.Equals(elements.GetName("/Subtype"), "/Widget", StringComparison.Ordinal);

  /// <summary>Dispatches a value to the writer for the field's type. A field type that
  ///     carries no fillable value refuses the fill rather than silently doing nothing.
  ///     </summary>
  private static Result<bool> Apply(PdfAcroField field, string value) => field switch
  {
    PdfTextField => SetText(field, value),
    PdfCheckBoxField => SetCheckbox(field, value),
    PdfRadioButtonField => SetRadio(field, value),
    PdfComboBoxField => SetChoice(field, value, "a combo box"),
    PdfListBoxField => SetChoice(field, value, "a list box"),
    PdfPushButtonField => Mismatch(field, "a push button", "a value"),
    PdfSignatureField => Mismatch(field, "a signature field", "a value"),
    _ => Mismatch(field, "an unsupported field type", $"the value '{value}'"),
  };

  /// <summary>Writes a text field's value.</summary>
  private static Result<bool> SetText(PdfAcroField field, string value)
  {
    field.Elements.SetString("/V", value);
    return Result.Success(true);
  }

  /// <summary>Checks or unchecks a checkbox. The value must parse as a boolean; the
  ///     widget's appearance state is set alongside the field value so the box renders
  ///     checked in viewers that read /AS.</summary>
  private static Result<bool> SetCheckbox(PdfAcroField field, string value)
  {
    if (!bool.TryParse(value, out bool on))
    {
      return Mismatch(field, "a checkbox", $"'{value}' (expected true or false)");
    }

    string state = on ? OnStateName(field) : "/Off";
    return SetToggleState(field, state);
  }

  /// <summary>Selects a radio group's option by its export value, or clears the group
  ///     when the value is false.</summary>
  private static Result<bool> SetRadio(PdfAcroField field, string value)
  {
    if (bool.TryParse(value, out bool on) && !on)
    {
      return SetToggleState(field, "/Off");
    }

    List<string> options = OptionNames(field);
    return options.Contains(value, StringComparer.Ordinal)
        ? SetToggleState(field, "/" + value)
        : Mismatch(field, "a radio group", $"'{value}' (expected one of {Describe(options)})");
  }

  /// <summary>Selects a choice field's option by its export value.</summary>
  private static Result<bool> SetChoice(PdfAcroField field, string value, string kind)
  {
    List<string> options = OptionNames(field);
    if (!options.Contains(value, StringComparer.Ordinal))
    {
      return Mismatch(field, kind, $"'{value}' (expected one of {Describe(options)})");
    }

    field.Elements.SetString("/V", value);
    return Result.Success(true);
  }

  /// <summary>Sets a toggle field's value and every widget's appearance state, which is
  ///     what a viewer draws from.</summary>
  private static Result<bool> SetToggleState(PdfAcroField field, string state)
  {
    field.Elements.SetName("/V", state);
    SetAppearanceState(field, state);
    if (field.Elements["/Kids"] is PdfArray kids)
    {
      foreach (PdfItem kid in kids.Elements)
      {
        if (kid is PdfDictionary widget)
        {
          widget.Elements.SetName("/AS", state);
        }
      }
    }

    return Result.Success(true);
  }

  /// <summary>Sets /AS on a widget, leaving a non-widget field dictionary alone.</summary>
  private static void SetAppearanceState(PdfAcroField field, string state)
  {
    if (IsWidgetElements(field.Elements))
    {
      field.Elements.SetName("/AS", state);
    }
  }

  /// <summary>The on-state name a checkbox declares in its appearance dictionary, or
  ///     '/Yes' when the document declares none.</summary>
  private static string OnStateName(PdfAcroField field)
  {
    if (field.Elements["/AP"] is PdfDictionary appearance
        && appearance.Elements["/N"] is PdfDictionary normal)
    {
      string? on = normal.Elements.Keys.FirstOrDefault(key => !string.Equals(key, "/Off", StringComparison.Ordinal));
      return on ?? "/Yes";
    }

    return "/Yes";
  }

  /// <summary>The export values a choice or radio field offers, in document order. An
  ///     /Opt entry is either a string or a two-element [export, display] array.</summary>
  private static List<string> OptionNames(PdfAcroField field)
  {
    return field.Elements["/Opt"] is PdfArray options
        ? [.. options.Elements.Select(ExportOf).OfType<string>()]
        : [];
  }

  /// <summary>One /Opt entry's export value, or null when the entry carries none.
  ///     </summary>
  private static string? ExportOf(PdfItem item) => item switch
  {
    PdfString text => text.Value,
    PdfArray pair when pair.Elements.Count == 2 && pair.Elements[0] is PdfString export => export.Value,
    _ => null,
  };

  /// <summary>Finds a field by fully qualified name.</summary>
  private static PdfAcroField? FindField(PdfDocument doc, string name)
  {
    PdfAcroForm? form = TryGetForm(doc);
    if (form is null)
    {
      return null;
    }

    try
    {
      return form.Fields[name];
    }
    catch (ArgumentException)
    {
      return null;
    }
  }

  /// <summary>The document's form, or null when it declares none. PDFsharp's accessor
  ///     throws on a document without one, so the catalog entry is the test.</summary>
  private static PdfAcroForm? TryGetForm(PdfDocument doc)
      => doc.Internals.Catalog.Elements.ContainsKey("/AcroForm") ? doc.AcroForm : null;

  /// <summary>Marks the form as needing appearance regeneration, so a viewer redraws
  ///     the values this batch wrote instead of showing the field's original pixels.
  ///     </summary>
  private static void MarkNeedsAppearances(PdfDocument doc)
      => TryGetForm(doc)?.Elements.SetBoolean("/NeedAppearances", true);

  /// <summary>The document's field names, for an error that tells the caller what it
  ///     could have named.</summary>
  private static string FieldNames(PdfDocument doc)
  {
    PdfAcroForm? form = TryGetForm(doc);
    return form is null ? "(none: the document has no form)" : Describe([.. form.Fields.Names]);
  }

  /// <summary>Renders a name list for an error message.</summary>
  private static string Describe(List<string> names)
      => names.Count == 0 ? "(none)" : string.Join(", ", names.Select(name => "'" + name + "'"));

  private static Result<bool> Mismatch(PdfAcroField field, string kind, string got)
      => Fail(PdfErrorCodes.FormFieldTypeMismatch,
          "field '" + field.Elements.GetString("/T") + "' is " + kind + " and cannot hold " + got + ".");

  private static Result<bool> Fail(string code, string message)
      => Result.Failure<bool>(new DomainError(code, message));

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }
}
