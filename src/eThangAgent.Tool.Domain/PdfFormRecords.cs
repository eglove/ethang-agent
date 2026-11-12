namespace eThangAgent.ToolDomain;

/// <summary>The closed vocabulary of form field kinds the pdf surface reports and
///     fills. It mirrors the PDF field types the ACL can both read and write; a field
///     of any other type is reported as <see cref="Unsupported"/> and refuses a fill
///     rather than being guessed at.</summary>
public enum PdfFormFieldKind
{
  /// <summary>A single- or multi-line text field.</summary>
  Text,

  /// <summary>A checkbox.</summary>
  Checkbox,

  /// <summary>One group of radio buttons, addressed as one field.</summary>
  Radio,

  /// <summary>A combo box or list box.</summary>
  Choice,

  /// <summary>A push button: it carries a caption, not a value, and cannot be filled.</summary>
  Button,

  /// <summary>A signature field: it cannot be filled by this surface.</summary>
  Signature,

  /// <summary>A field whose type the document does not declare or the ACL does not
  ///     model.</summary>
  Unsupported,
}

/// <summary>One form field: its fully qualified name, its kind, the 1-based page its
///     widget sits on (null when the field declares no page), its current value, and -
///     for a choice field - the export values it accepts. <see cref="Value"/> is the
///     field's value as the document states it: a text field's text, a checkbox's or
///     radio group's selected export value (empty when nothing is selected), and a
///     choice field's selected export value.</summary>
public sealed record PdfFormField(
    string Name,
    PdfFormFieldKind Kind,
    int? Page,
    string Value,
    IReadOnlyList<string> Options);

/// <summary>The form read snapshot: the file's content-true version at the read
///     moment, the document's total page count, and every field in document order.
///     An empty field list means the document carries no form.</summary>
public sealed record PdfFormSnapshot(
    FileVersion Version,
    int TotalPages,
    IReadOnlyList<PdfFormField> Fields);
