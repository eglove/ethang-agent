using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms;
using UglyToad.PdfPig.AcroForms.Fields;

namespace eThangAgent.PDF.ACL.Tests;

/// <summary>AcroForm support: reading a document's fields, filling them, and flattening
///     the form. Every assertion reads the result back through PdfPig, independently of
///     the code that wrote it.</summary>
public sealed class PdfFormTests : IDisposable
{
  private readonly DirectPdfAccess _access = new();
  private readonly List<string> _dirs = [];

  public void Dispose()
  {
    foreach (string dir in _dirs)
    {
      try
      {
        Directory.Delete(dir, true);
      }
      catch (IOException)
      {
        // best-effort temp cleanup
      }
      catch (UnauthorizedAccessException)
      {
        // best-effort temp cleanup
      }
    }

    GC.SuppressFinalize(this);
  }

  private string NewForm(string name = "form.pdf", params string[] options)
  {
    string dir = Path.Combine(TestTempRoot.Root, "pdf-form-tests-" + Guid.NewGuid().ToString("N"));
    _dirs.Add(dir);
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, name);
    TestPdfFactory.CreateForm(path, options.Length == 0 ? ["GB", "US"] : options);
    return path;
  }

  private static FileVersion VersionOf(string path)
  {
    FileInfo info = new(path);
    return new FileVersion(info.Length, info.LastWriteTimeUtc);
  }

  private static AcroForm FormOf(string path)
  {
    using PdfDocument doc = PdfDocument.Open(path);
    return doc.TryGetForm(out AcroForm? form) ? form : throw new InvalidOperationException("no form");
  }

  private static AcroFieldBase FieldOf(string path, string name)
      => FormOf(path).Fields.First(field => field.Information.PartialName == name);

  [Fact]
  public async Task ReadForm_reports_every_field_with_kind_page_and_options()
  {
    string path = NewForm();

    Result<PdfFormSnapshot> r = await _access.ReadFormAsync(path, TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(1, r.Value.TotalPages);
    Assert.Equal(
        ["fullName", "agree", "country", "submit"],
        r.Value.Fields.Select(f => f.Name));
    Assert.Equal(
        [PdfFormFieldKind.Text, PdfFormFieldKind.Checkbox, PdfFormFieldKind.Choice, PdfFormFieldKind.Button],
        r.Value.Fields.Select(f => f.Kind));
    Assert.All(r.Value.Fields, f => Assert.Equal(1, f.Page));
    Assert.Equal(["GB", "US"], r.Value.Fields[2].Options);
  }

  [Fact]
  public async Task ReadForm_reports_the_version_a_fill_must_pass_back()
  {
    string path = NewForm();

    Result<PdfFormSnapshot> r = await _access.ReadFormAsync(path, TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Equal(VersionOf(path), r.Value.Version);
  }

  [Fact]
  public async Task ReadForm_on_a_document_without_a_form_is_an_empty_field_list()
  {
    string dir = Path.Combine(TestTempRoot.Root, "pdf-form-tests-" + Guid.NewGuid().ToString("N"));
    _dirs.Add(dir);
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "plain.pdf");
    TestPdfFactory.Create(path, "no form here");

    Result<PdfFormSnapshot> r = await _access.ReadFormAsync(path, TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    Assert.Empty(r.Value.Fields);
  }

  [Fact]
  public async Task ReadForm_refuses_a_missing_file()
  {
    Result<PdfFormSnapshot> r = await _access.ReadFormAsync(
        Path.Combine(TestTempRoot.Root, "absent-" + Guid.NewGuid().ToString("N") + ".pdf"),
        TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.FileNotFound, r.Error.Code);
  }

  [Fact]
  public async Task Manipulate_fills_a_text_field()
  {
    string path = NewForm();

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new FillFormField("fullName", "Ada Lovelace")], TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    AcroTextField field = Assert.IsType<AcroTextField>(FieldOf(path, "fullName"));
    Assert.Equal("Ada Lovelace", field.Value);
  }

  [Fact]
  public async Task Manipulate_checks_a_checkbox_and_clears_it_again()
  {
    string path = NewForm();

    Result<PdfEditOutcome> on = await _access.ManipulateAsync(
        path, VersionOf(path), [new FillFormField("agree", "true")], TestContext.Current.CancellationToken);
    Assert.True(on.IsSuccess, on.Error?.Message);
    Assert.True(Assert.IsType<AcroCheckboxField>(FieldOf(path, "agree")).IsChecked);

    Result<PdfEditOutcome> off = await _access.ManipulateAsync(
        path, VersionOf(path), [new FillFormField("agree", "false")], TestContext.Current.CancellationToken);
    Assert.True(off.IsSuccess, off.Error?.Message);
    Assert.False(Assert.IsType<AcroCheckboxField>(FieldOf(path, "agree")).IsChecked);
  }

  [Fact]
  public async Task Manipulate_selects_a_choice_option()
  {
    string path = NewForm();

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new FillFormField("country", "US")], TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    AcroListBoxField field = Assert.IsType<AcroListBoxField>(FieldOf(path, "country"));
    Assert.Equal("US", Assert.Single(field.SelectedOptions));
  }

  [Fact]
  public async Task Manipulate_refuses_an_unknown_field_naming_the_fields_that_exist()
  {
    string path = NewForm();

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new FillFormField("nope", "x")], TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.FormFieldNotFound, r.Error.Code);
    Assert.Contains("fullName", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Manipulate_refuses_a_value_the_field_kind_cannot_hold()
  {
    string path = NewForm();

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new FillFormField("agree", "maybe")], TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.FormFieldTypeMismatch, r.Error.Code);
  }

  [Fact]
  public async Task Manipulate_refuses_a_choice_value_that_is_not_an_option()
  {
    string path = NewForm();

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new FillFormField("country", "FR")], TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.FormFieldTypeMismatch, r.Error.Code);
    Assert.Contains("GB", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Manipulate_refuses_to_fill_a_push_button()
  {
    string path = NewForm();

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new FillFormField("submit", "x")], TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.FormFieldTypeMismatch, r.Error.Code);
  }

  [Fact]
  public async Task Manipulate_failed_fill_saves_nothing()
  {
    string path = NewForm();
    byte[] before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new FillFormField("fullName", "Ada"), new FillFormField("nope", "x")],
        TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    Assert.True(string.IsNullOrEmpty(Assert.IsType<AcroTextField>(FieldOf(path, "fullName")).Value));
  }

  [Fact]
  public async Task Manipulate_fills_and_flattens_in_one_batch()
  {
    string path = NewForm();

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path),
        [new FillFormField("fullName", "Ada Lovelace"), new FlattenForm(null)],
        TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    using PdfDocument doc = PdfDocument.Open(path);
    Assert.False(doc.TryGetForm(out _), "the form should be gone after a flatten");
    string text = doc.GetPage(1).Text;
    Assert.Contains("Body text", text, StringComparison.Ordinal);

    // The filled value survives the flatten as page content, which is the point of
    // flattening: the document still shows what was entered, it just cannot be edited.
    Assert.Contains("Ada Lovelace", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Manipulate_flatten_leaves_a_document_without_a_form_alone()
  {
    string dir = Path.Combine(TestTempRoot.Root, "pdf-form-tests-" + Guid.NewGuid().ToString("N"));
    _dirs.Add(dir);
    _ = Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "plain.pdf");
    TestPdfFactory.Create(path, "no form here");

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, VersionOf(path), [new FlattenForm(null)], TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.Error?.Message);
    using PdfDocument doc = PdfDocument.Open(path);
    Assert.Contains("no form here", doc.GetPage(1).Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Manipulate_refuses_a_stale_version_before_filling()
  {
    string path = NewForm();
    FileVersion stale = new(1, DateTime.UnixEpoch);

    Result<PdfEditOutcome> r = await _access.ManipulateAsync(
        path, stale, [new FillFormField("fullName", "Ada")], TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal(PdfErrorCodes.StaleDocument, r.Error.Code);
  }
}
