using DocumentFormat.OpenXml.Packaging;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>Chart data editing: values, series names and category names written to a
///     chart the deck already has, with the chart's cached values and its embedded
///     workbook kept in agreement, and the refusals that leave the file byte-identical.
///     </summary>
public sealed class PptxChartEditTests
{
  private readonly DirectPptxAccess _access = new();

  private static string ScratchFile(string name)
    => Path.Combine(TestTempRoot.CreateDirectory("pptx-chart").FullName, name);

  [Fact]
  public async Task Writes_values_that_read_back_from_the_chart()
  {
    string path = ChartDeck("values.pptx");
    _ = await Edit(path, (await Outline(path)).Version,
        new SetChartData("s1.sh3", null, null, [[10d, 20d, 30d], [40d, 50d, 60d]]));
    Assert.Equal([10d, 20d, 30d], Values(path, 0));
    Assert.Equal([40d, 50d, 60d], Values(path, 1));
  }

  [Fact]
  public async Task Writes_the_same_values_into_the_embedded_workbook()
  {
    string path = ChartDeck("workbook.pptx");
    _ = await Edit(path, (await Outline(path)).Version,
        new SetChartData("s1.sh3", null, null, [[10d, 20d, 30d], [40d, 50d, 60d]]));
    IReadOnlyList<IReadOnlyList<string>> sheet = Sheet(path);
    Assert.Equal(["Series 1", "Series 2"], sheet[0]);
    Assert.Equal(["Cat A", "Cat B", "Cat C"], sheet.Skip(1).Select(row => row[0]));
    Assert.Equal("10", sheet[1][1]);
    Assert.Equal("60", sheet[3][2]);
  }

  [Fact]
  public async Task Renames_series_and_categories_in_the_chart_and_the_workbook()
  {
    string path = ChartDeck("rename.pptx");
    _ = await Edit(path, (await Outline(path)).Version,
        new SetChartData("s1.sh3", ["Revenue", "Cost"], ["Q1", "Q2", "Q3"], null));
    PptxChartShape chart = await Chart(path);
    Assert.Equal(["Revenue", "Cost"], chart.SeriesNames);
    Assert.Equal(["Q1", "Q2", "Q3"], chart.CategoryNames);
    IReadOnlyList<IReadOnlyList<string>> sheet = Sheet(path);
    Assert.Equal(["Revenue", "Cost"], sheet[0]);
    Assert.Equal(["Q1", "Q2", "Q3"], sheet.Skip(1).Select(row => row[0]));
  }

  [Fact]
  public async Task Adds_a_data_point_so_the_category_count_grows()
  {
    string path = ChartDeck("add-point.pptx");
    _ = await Edit(path, (await Outline(path)).Version,
        new SetChartData("s1.sh3", null, ["Cat A", "Cat B", "Cat C", "Cat D"],
            [[1d, 2d, 3d, 4d], [5d, 6d, 7d, 8d]]));
    PptxChartShape chart = await Chart(path);
    Assert.Equal(4, chart.CategoryNames.Count);
    Assert.Equal("Cat D", chart.CategoryNames[3]);
    Assert.Equal([1d, 2d, 3d, 4d], Values(path, 0));
    Assert.Equal(5, Sheet(path).Count);
  }

  [Fact]
  public async Task Removes_a_data_point_so_the_category_count_shrinks()
  {
    string path = ChartDeck("remove-point.pptx");
    _ = await Edit(path, (await Outline(path)).Version,
        new SetChartData("s1.sh3", null, ["Cat A", "Cat B"], [[1d, 2d], [3d, 4d]]));
    Assert.Equal(["Cat A", "Cat B"], (await Chart(path)).CategoryNames);
    Assert.Equal(3, Sheet(path).Count);
  }

  [Fact]
  public async Task Refuses_values_whose_shape_does_not_match_the_chart()
  {
    string path = ChartDeck("shape.pptx");
    byte[] before = await Bytes(path);
    Result<PptxEditOutcome> result = await TryEdit(path, new SetChartData("s1.sh3", null, null, [[1d, 2d]]));
    Assert.False(result.IsSuccess);
    Assert.Equal(PptxErrorCodes.InvalidAddress, result.Error.Code);
    Assert.Contains("2 series of 3 values", result.Error.Message, StringComparison.Ordinal);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_series_names_that_do_not_match_the_series_count()
  {
    string path = ChartDeck("names.pptx");
    byte[] before = await Bytes(path);
    Result<PptxEditOutcome> result = await TryEdit(path, new SetChartData("s1.sh3", ["Only one"], null, null));
    Assert.False(result.IsSuccess);
    Assert.Equal(PptxErrorCodes.InvalidAddress, result.Error.Code);
    Assert.Contains("2 series names are expected", result.Error.Message, StringComparison.Ordinal);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_a_chart_whose_embedded_workbook_is_missing()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("no-workbook.pptx"),
        [new TestSlideSpec { Title = "T", WithChart = true }]);
    byte[] before = await Bytes(path);
    Result<PptxEditOutcome> result = await TryEdit(path, new SetChartData("s1.sh3", null, null, [[7d, 8d]]));
    Assert.False(result.IsSuccess);
    Assert.Equal(PptxErrorCodes.ChartDataMissing, result.Error.Code);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Refuses_a_chart_type_outside_the_supported_families_naming_it()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("radar.pptx"),
        [new TestSlideSpec { Title = "T", WithChart = true, ChartFamily = "radar", WithChartWorkbook = true }]);
    byte[] before = await Bytes(path);
    Result<PptxEditOutcome> result = await TryEdit(path, new SetChartData("s1.sh3", null, null, [[1d, 2d]]));
    Assert.False(result.IsSuccess);
    Assert.Equal(PptxErrorCodes.ChartTypeUnsupported, result.Error.Code);
    Assert.Contains("radar", result.Error.Message, StringComparison.Ordinal);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Edits_a_line_chart_and_a_pie_chart()
  {
    string line = TestPptxFactory.CreateDeck(ScratchFile("line.pptx"),
        [new TestSlideSpec { Title = "T", WithChart = true, ChartFamily = "line", WithChartWorkbook = true }]);
    _ = await Edit(line, (await Outline(line)).Version, new SetChartData("s1.sh3", null, null, [[9d, 8d]]));
    Assert.Equal([9d, 8d], Values(line, 0));
    string pie = TestPptxFactory.CreateDeck(ScratchFile("pie.pptx"),
        [new TestSlideSpec { Title = "T", WithChart = true, ChartFamily = "pie", WithChartWorkbook = true }]);
    _ = await Edit(pie, (await Outline(pie)).Version, new SetChartData("s1.sh3", null, null, [[3d, 4d]]));
    Assert.Equal([3d, 4d], Values(pie, 0));
  }

  [Fact]
  public async Task Leaves_the_chart_type_and_its_axes_alone()
  {
    string path = ChartDeck("type.pptx");
    _ = await Edit(path, (await Outline(path)).Version, new SetChartData("s1.sh3", null, null, [[5d, 5d, 5d], [6d, 6d, 6d]]));
    Assert.Equal("column", (await Chart(path)).ChartType);
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    C.PlotArea area = ChartSpaceOf(doc).GetFirstChild<C.Chart>()!.PlotArea!;
    Assert.NotNull(area.GetFirstChild<C.BarChart>());
    Assert.Equal("col", area.GetFirstChild<C.BarChart>()!.BarDirection!.Val!.InnerText);
    Assert.Equal(2, area.Elements<C.CategoryAxis>().Count() + area.Elements<C.ValueAxis>().Count());
  }

  [Fact]
  public async Task Refuses_an_address_that_names_no_chart()
  {
    string path = ChartDeck("not-a-chart.pptx");
    byte[] before = await Bytes(path);
    Result<PptxEditOutcome> result = await TryEdit(path, new SetChartData("s1.sh1", null, null, [[1d, 2d]]));
    Assert.False(result.IsSuccess);
    Assert.Equal(PptxErrorCodes.AddressNotFound, result.Error.Code);
    Assert.Equal(before, await Bytes(path));
  }

  [Fact]
  public async Task Changes_nothing_else_in_the_deck()
  {
    string path = TestPptxFactory.CreateDeck(ScratchFile("fidelity.pptx"),
    [
      new TestSlideSpec { Title = "One", Body = "Body one", Notes = "Notes one", WithChart = true, WithChartWorkbook = true },
      new TestSlideSpec { Title = "Two", Body = "Body two", Hidden = true },
    ]);
    PptxOutline before = await Outline(path);
    string secondBefore = ShapeXml(path, 2, 1);
    _ = await Edit(path, before.Version, new SetChartData("s1.sh3", null, null, [[7d, 7d]]));
    PptxOutline after = await Outline(path);
    Assert.Equal(before.SlideCount, after.SlideCount);
    Assert.Equal(before.LayoutNames, after.LayoutNames);
    Assert.Equal(secondBefore, ShapeXml(path, 2, 1));
    Assert.Equal("Notes one", (await SlideRead(path, 1)).Notes);
  }

  [Fact]
  public async Task Reports_the_chart_as_one_changed_address()
  {
    string path = ChartDeck("changed-count.pptx");
    PptxEditOutcome outcome = await Edit(path, (await Outline(path)).Version,
        new SetChartData("s1.sh3", null, null, [[1d, 1d, 1d], [2d, 2d, 2d]]));
    Assert.Equal(1, outcome.ChangedCount);
  }

  /// <summary>A two-slide deck whose first slide carries a two-series, three-category
  ///     column chart with an embedded workbook, at s1.sh3.</summary>
  private static string ChartDeck(string name)
    => TestPptxFactory.CreateDeck(ScratchFile(name),
    [
      new TestSlideSpec
      {
        Title = "One",
        WithChart = true,
        WithChartWorkbook = true,
        ChartSeriesNames = ["Series 1", "Series 2"],
        ChartCategories = ["Cat A", "Cat B", "Cat C"],
        ChartValues = [[1d, 2d, 3d], [4d, 5d, 6d]],
      },
      new TestSlideSpec { Title = "Two" },
    ]);

  /// <summary>The chart's cached values for one series, read from the chart part.
  ///     </summary>
  private static IReadOnlyList<double> Values(string path, int series)
  {
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    C.ChartSpace space = ChartSpaceOf(doc);
    C.NumberingCache cache = space.Descendants<C.NumberingCache>().ElementAt(series);
    return [.. cache.Elements<C.NumericPoint>().Select(point => double.Parse(point.NumericValue!.Text,
        System.Globalization.CultureInfo.InvariantCulture))];
  }

  /// <summary>The embedded workbook's first sheet, row by row, as text.</summary>
  private static IReadOnlyList<IReadOnlyList<string>> Sheet(string path)
  {
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    EmbeddedPackagePart workbook = ChartPartOf(doc).EmbeddedPackagePart!;
    using MemoryStream buffer = new();
    using (Stream stream = workbook.GetStream())
    {
      stream.CopyTo(buffer);
    }

    buffer.Position = 0;
    using SpreadsheetDocument sheet = SpreadsheetDocument.Open(buffer, false);
    S.SheetData data = sheet.WorkbookPart!.WorksheetParts.First().Worksheet!.GetFirstChild<S.SheetData>()!;
    return [.. data.Elements<S.Row>().Select(row => (IReadOnlyList<string>)[.. row.Elements<S.Cell>()
        .Select(cell => cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? string.Empty)])];
  }

  private static ChartPart ChartPartOf(PresentationDocument doc)
    => doc.PresentationPart!.SlideParts.First().ChartParts.First();

  private static C.ChartSpace ChartSpaceOf(PresentationDocument doc)
    => ChartPartOf(doc).ChartSpace!;

  private static async Task<byte[]> Bytes(string path)
    => await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);

  private static string ShapeXml(string path, int slide, int index)
  {
    using PresentationDocument doc = PresentationDocument.Open(path, false);
    SlidePart part = doc.PresentationPart!.SlideParts.ElementAt(slide - 1);
    return part.Slide!.CommonSlideData!.ShapeTree!.ChildElements
        .Where(child => child.LocalName is "sp" or "pic" or "graphicFrame" or "grpSp" or "cxnSp")
        .ElementAt(index - 1).OuterXml;
  }

  private async Task<PptxChartShape> Chart(string path)
  {
    PptxSlideSnapshot slide = await SlideRead(path, 1).ConfigureAwait(true);
    return Assert.IsType<PptxChartShape>(slide.Shapes[2]);
  }

  /// <summary>The edit's result, resolved against the file's current version, for a test
  ///     that asserts a refusal rather than a change.</summary>
  private async Task<Result<PptxEditOutcome>> TryEdit(string path, params PptxOperation[] operations)
  {
    FileVersion version = (await Outline(path).ConfigureAwait(true)).Version;
    return await _access.EditAsync(path, version, operations, TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  private async Task<PptxEditOutcome> Edit(string path, FileVersion version, params PptxOperation[] operations)
  {
    Result<PptxEditOutcome> result = await _access.EditAsync(path, version, operations).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<PptxOutline> Outline(string path)
  {
    Result<PptxOutline> result = await _access.ReadOutlineAsync(path).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }

  private async Task<PptxSlideSnapshot> SlideRead(string path, int slide)
  {
    Result<PptxSlideSnapshot> result = await _access.ReadSlideAsync(path, slide).ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    return result.Value;
  }
}
