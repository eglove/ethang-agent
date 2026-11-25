using System.IO.Compression;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>Format sniffing for the pptx surface: the zip magic, the presence part, the
///     macro part, the extension gate, the size cap, and the corrupt-package refusal -
///     each against a fixture built for it.</summary>
public sealed class PptxFormatSnifferTests
{
  private static string ScratchFile(string name)
  {
    DirectoryInfo dir = TestTempRoot.CreateDirectory("pptx-sniff");
    return Path.Combine(dir.FullName, name);
  }

  [Fact]
  public void Accepts_a_factory_deck()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("deck.pptx"));
    Result<string> sniff = PptxFormatSniffer.Inspect(path);
    Assert.True(sniff.IsSuccess, sniff.Error?.Message);
    Assert.Equal("pptx", sniff.Value);
  }

  [Fact]
  public void Rejects_a_text_file()
  {
    string path = ScratchFile("plain.pptx");
    File.WriteAllText(path, "hello");
    Assert.Equal(PptxErrorCodes.NotAPptx, InspectCode(path));
  }

  [Fact]
  public void Rejects_an_empty_file()
  {
    string path = ScratchFile("empty.pptx");
    File.WriteAllBytes(path, []);
    Assert.Equal(PptxErrorCodes.NotAPptx, InspectCode(path));
  }

  [Fact]
  public void Rejects_a_binary_ppt()
  {
    string path = ScratchFile("legacy.ppt");
    File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
    Assert.Equal(PptxErrorCodes.NotAPptx, InspectCode(path));
  }

  [Fact]
  public void Rejects_a_missing_file()
  {
    string path = ScratchFile("absent.pptx");
    Assert.Equal(PptxErrorCodes.FileNotFound, InspectCode(path));
  }

  [Fact]
  public void Rejects_a_zip_without_a_presentation_part()
  {
    string path = ScratchFile("archive.pptx");
    using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
    {
      WriteEntry(archive, "[Content_Types].xml", "<Types/>");
      WriteEntry(archive, "word/document.xml", "<document/>");
    }

    Assert.Equal(PptxErrorCodes.NotAPptx, InspectCode(path));
  }

  [Fact]
  public void Rejects_a_slide_show_extension()
  {
    string source = TestPptxFactory.CreateMinimalDeck(ScratchFile("source.pptx"));
    string path = ScratchFile("show.ppsx");
    File.Copy(source, path);
    Assert.Equal(PptxErrorCodes.NotAPptx, InspectCode(path));
  }

  [Fact]
  public void Rejects_a_template_extension()
  {
    string source = TestPptxFactory.CreateMinimalDeck(ScratchFile("source.pptx"));
    string path = ScratchFile("template.potx");
    File.Copy(source, path);
    Assert.Equal(PptxErrorCodes.NotAPptx, InspectCode(path));
  }

  [Fact]
  public void Rejects_a_macro_presentation()
  {
    string path = TestPptxFactory.CreateMinimalDeck(ScratchFile("macro.pptx"));
    using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Update))
    {
      WriteEntry(archive, PptxFormatSniffer.MacroPartName, "stub");
    }

    Assert.Equal(PptxErrorCodes.MacroPresentation, InspectCode(path));
  }

  [Fact]
  public void Rejects_a_truncated_package()
  {
    string source = TestPptxFactory.CreateMinimalDeck(ScratchFile("source.pptx"));
    byte[] bytes = File.ReadAllBytes(source);
    string path = ScratchFile("truncated.pptx");
    File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
    Assert.Equal(PptxErrorCodes.CorruptPresentation, InspectCode(path));
  }

  [Fact]
  public void Rejects_a_file_past_the_size_cap()
  {
    string path = ScratchFile("oversize.pptx");
    using (FileStream stream = File.Create(path))
    {
      stream.SetLength(PptxLimits.MaxFileBytes + 1);
    }

    Assert.Equal(PptxErrorCodes.OversizePresentation, InspectCode(path));
  }

  [Fact]
  public void Limits_are_the_pinned_numbers()
  {
    Assert.Equal(104_857_600L, PptxLimits.MaxFileBytes);
    Assert.Equal(1000, PptxLimits.MaxSlides);
  }

  private static string InspectCode(string path)
  {
    Result<string> sniff = PptxFormatSniffer.Inspect(path);
    Assert.False(sniff.IsSuccess);
    return sniff.Error.Code;
  }

  private static void WriteEntry(ZipArchive archive, string name, string content)
  {
    using StreamWriter writer = new(archive.CreateEntry(name).Open());
    writer.Write(content);
  }
}
