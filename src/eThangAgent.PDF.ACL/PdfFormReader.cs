using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms;
using UglyToad.PdfPig.AcroForms.Fields;
using UglyToad.PdfPig.Exceptions;

namespace eThangAgent.PDF.ACL;

/// <summary>The form read path over PdfPig: stats the file FIRST for the content-true
///     <see cref="FileVersion"/>, gates the open through <see cref="PdfFormatSniffer"/>,
///     then reports every AcroForm field in document order. A document with no form is an
///     empty field list, never a failure. Library faults map to the canonical codes;
///     unknown failures become CorruptPdf with the message clamped to ~300 characters.
///     </summary>
public static class PdfFormReader
{
  /// <summary>Maps PdfPig's field type onto the domain vocabulary. The plural forms
  ///     PdfPig reports for a set of checkboxes or radio buttons name the same kind as the
  ///     singular: the tool addresses the group, not an individual button.</summary>
  private static readonly Dictionary<AcroFieldType, PdfFormFieldKind> Kinds = new()
  {
    [AcroFieldType.Text] = PdfFormFieldKind.Text,
    [AcroFieldType.Checkbox] = PdfFormFieldKind.Checkbox,
    [AcroFieldType.Checkboxes] = PdfFormFieldKind.Checkbox,
    [AcroFieldType.RadioButton] = PdfFormFieldKind.Radio,
    [AcroFieldType.RadioButtons] = PdfFormFieldKind.Radio,
    [AcroFieldType.ComboBox] = PdfFormFieldKind.Choice,
    [AcroFieldType.ListBox] = PdfFormFieldKind.Choice,
    [AcroFieldType.PushButton] = PdfFormFieldKind.Button,
    [AcroFieldType.Signature] = PdfFormFieldKind.Signature,
  };

  /// <summary>Reads the form snapshot, or a typed <see cref="DomainError"/>.</summary>
  public static Result<PdfFormSnapshot> Read(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    if (!File.Exists(path))
    {
      return Fail(PdfErrorCodes.FileNotFound, $"File not found: '{path}'.");
    }

    // Stat FIRST: the version is the pair captured before the document is opened.
    FileInfo info = new(path);
    FileVersion version = new(info.Length, info.LastWriteTimeUtc);

    Result<string> sniff = PdfFormatSniffer.Inspect(path);
    if (!sniff.IsSuccess)
    {
      return Result.Failure<PdfFormSnapshot>(sniff.Error);
    }

    try
    {
      using PdfDocument doc = PdfDocument.Open(path);
      List<PdfFormField> fields = doc.TryGetForm(out AcroForm? form)
          ? [.. form.Fields.Select(ToField)]
          : [];
      return Result.Success(new PdfFormSnapshot(version, doc.NumberOfPages, fields));
    }
    catch (PdfDocumentEncryptedException ex)
    {
      return Fail(PdfErrorCodes.EncryptedPdf, $"PDF file '{path}' is encrypted: {Clamp(ex.Message)}");
    }
#pragma warning disable CA1031 // Named decision: the open/read boundary maps every library fault to a typed DomainError; an unknown failure must never escape as an exception.
    catch (Exception ex)
    {
      return Fail(PdfErrorCodes.CorruptPdf, $"'{path}' could not be read as a PDF: {Clamp(ex.Message)}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Projects one PdfPig field onto the domain record. PdfPig resolves the
  ///     field hierarchy, so the partial name it reports is the fully qualified name the
  ///     fill path addresses the field by.</summary>
  private static PdfFormField ToField(AcroFieldBase field) => new(
      field.Information.PartialName ?? string.Empty,
      Kinds.TryGetValue(field.FieldType, out PdfFormFieldKind kind) ? kind : PdfFormFieldKind.Unsupported,
      field.PageNumber,
      ValueOf(field),
      OptionsOf(field));

  /// <summary>The field's current value in the document's own terms: a text field's
  ///     text, a checkbox's or radio group's selected export value (empty when nothing is
  ///     selected), a choice field's selected export value, and empty for a push button or
  ///     signature, which carry no fillable value.</summary>
  private static string ValueOf(AcroFieldBase field) => field switch
  {
    AcroTextField text => text.Value ?? string.Empty,
    AcroCheckboxField checkbox => Toggled(checkbox.IsChecked, checkbox.CurrentValue),
    AcroRadioButtonField radio => Toggled(radio.IsSelected, radio.CurrentValue),
    AcroComboBoxField combo => Selected(combo.SelectedOptions),
    AcroListBoxField list => Selected(list.SelectedOptions),
    _ => string.Empty,
  };

  /// <summary>A toggle's selected export value, or empty when it is not selected.
  ///     </summary>
  private static string Toggled(bool selected, string? currentValue)
      => selected ? currentValue ?? string.Empty : string.Empty;

  /// <summary>The export values a choice field offers, in document order.</summary>
  private static List<string> OptionsOf(AcroFieldBase field) => field switch
  {
    AcroComboBoxField combo => Exports(combo.Options),
    AcroListBoxField list => Exports(list.Options),
    _ => [],
  };

  /// <summary>A choice field's option export values.</summary>
  private static List<string> Exports(IReadOnlyList<AcroChoiceOption> options)
      => [.. options.Select(option => option.ExportValue ?? option.Name ?? string.Empty)];

  /// <summary>The first selected option's export value, or empty when none is selected.
  ///     </summary>
  private static string Selected(IReadOnlyList<string> selected)
      => selected.Count == 0 ? string.Empty : selected[0];

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }

  private static Result<PdfFormSnapshot> Fail(string code, string message)
      => Result.Failure<PdfFormSnapshot>(new DomainError(code, message));
}
