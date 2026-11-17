using System.Buffers.Binary;
using eThangAgent.TestSupport;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>A PATH entry is listed when the FILE says it is runnable — a batch file, or an
///     executable whose PE optional header names the console subsystem. Nothing here is a rule
///     about a name, a folder, or a size, so every test below builds the file it is about and
///     asserts on what the classification made of it.</summary>
public class PathToolDiscoveryTests
{
  /// <summary>Offset of the field holding the file offset of the PE signature.</summary>
  private const int PeHeaderOffsetField = 0x3C;

  /// <summary>Where the synthetic images put their PE signature.</summary>
  private const int SignatureOffset = 0x80;

  /// <summary>Distance from the signature to the optional header's Subsystem field.</summary>
  private const int SubsystemFromSignature = 4 + 20 + 68;

  private const ushort ConsoleSubsystem = 3;

  private const ushort GuiSubsystem = 2;

  // ---- Kind: the file decides, and only two kinds are listed ----

  [Fact]
  public void ConsoleSubsystemExecutable_IsListedAsConsoleProgram()
  {
    string directory = NewDirectory();
    WriteExecutable(directory, "rg.exe", ConsoleSubsystem);

    PathTool tool = Assert.Single(PathToolDiscovery.Scan([directory]).Tools);

    Assert.Equal("rg", tool.Name);
    Assert.Equal(directory, tool.SourceDirectory);
    Assert.Equal(Path.Combine(directory, "rg.exe"), tool.FullPath);
    Assert.Equal(PathToolKind.ConsoleProgram, tool.Kind);
  }

  [Fact]
  public void GuiSubsystemExecutable_IsAbsentFromTheResult()
  {
    string directory = NewDirectory();
    WriteExecutable(directory, "calc.exe", GuiSubsystem);
    WriteExecutable(directory, "rg.exe", ConsoleSubsystem);

    PathTool tool = Assert.Single(PathToolDiscovery.Scan([directory]).Tools);

    Assert.Equal("rg", tool.Name);
  }

  [Fact]
  public void CmdFile_IsListedAsScript_AndIsNeverReadAsAPeImage()
  {
    string directory = NewDirectory();
    File.WriteAllText(Path.Combine(directory, "npm.cmd"), "@echo off\r\necho 11.20.0\r\n");

    PathTool tool = Assert.Single(PathToolDiscovery.Scan([directory]).Tools);

    Assert.Equal("npm", tool.Name);
    Assert.Equal(PathToolKind.Script, tool.Kind);
  }

  [Fact]
  public void BatFile_IsListedAsScript()
  {
    string directory = NewDirectory();
    File.WriteAllText(Path.Combine(directory, "build.bat"), "@echo off\r\n");

    PathTool tool = Assert.Single(PathToolDiscovery.Scan([directory]).Tools);

    Assert.Equal("build", tool.Name);
    Assert.Equal(PathToolKind.Script, tool.Kind);
  }

  [Fact]
  public void NonPeHeader_IsAbsent_AndTheScanStillSucceeds()
  {
    string directory = NewDirectory();
    File.WriteAllBytes(Path.Combine(directory, "junk.exe"), new byte[512]);
    WriteExecutable(directory, "rg.exe", ConsoleSubsystem);

    PathTool tool = Assert.Single(PathToolDiscovery.Scan([directory]).Tools);

    Assert.Equal("rg", tool.Name);
  }

  [Fact]
  public void TruncatedHeader_IsAbsent()
  {
    string directory = NewDirectory();
    File.WriteAllBytes(Path.Combine(directory, "short.exe"), [0x4D, 0x5A, 0x00, 0x00]);

    Assert.Empty(PathToolDiscovery.Scan([directory]).Tools);
  }

  [Fact]
  public void SignatureOffsetBeyondTheEndOfTheFile_IsAbsent()
  {
    string directory = NewDirectory();
    byte[] image = new byte[512];
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(PeHeaderOffsetField), 0x7000);
    File.WriteAllBytes(Path.Combine(directory, "far.exe"), image);

    Assert.Empty(PathToolDiscovery.Scan([directory]).Tools);
  }

  [Fact]
  public void ZeroByteExecutable_IsAbsent_BecauseItsHeaderCannotBeRead()
  {
    // A WindowsApps app-execution alias is a 0-byte reparse point the operating system
    // refuses to open, so no subsystem can be read from it. It is not junk and it is not
    // filtered by size: it is unreadable, and the free-text add box is how a user names one.
    string directory = NewDirectory();
    File.WriteAllBytes(Path.Combine(directory, "winget.exe"), []);

    Assert.Empty(PathToolDiscovery.Scan([directory]).Tools);
  }

  [Fact]
  public void OtherExtensions_AreIgnored()
  {
    string directory = NewDirectory();
    File.WriteAllText(Path.Combine(directory, "notes.txt"), "hello");
    File.WriteAllText(Path.Combine(directory, "lib.dll"), "hello");
    File.WriteAllText(Path.Combine(directory, "run.ps1"), "hello");
    File.WriteAllText(Path.Combine(directory, "shim"), "hello");

    Assert.Empty(PathToolDiscovery.Scan([directory]).Tools);
  }

  // ---- Order: PATH order decides which copy of a name is listed ----

  [Fact]
  public void SameNameInTwoDirectories_IsListedOnceAgainstTheEarlierDirectory()
  {
    string first = NewDirectory();
    string second = NewDirectory();
    File.WriteAllText(Path.Combine(first, "npm.cmd"), "@echo off\r\n");
    File.WriteAllText(Path.Combine(second, "npm.cmd"), "@echo off\r\n");

    PathTool tool = Assert.Single(PathToolDiscovery.Scan([first, second]).Tools);

    Assert.Equal(first, tool.SourceDirectory);
    Assert.Equal(Path.Combine(first, "npm.cmd"), tool.FullPath);
  }

  [Fact]
  public void SameNameInThreeDirectories_IsListedOnce()
  {
    string[] directories = [NewDirectory(), NewDirectory(), NewDirectory()];
    foreach (string directory in directories)
    {
      File.WriteAllText(Path.Combine(directory, "pnpm.cmd"), "@echo off\r\n");
    }

    PathTool tool = Assert.Single(PathToolDiscovery.Scan(directories).Tools);

    Assert.Equal(directories[0], tool.SourceDirectory);
  }

  [Fact]
  public void EarlierCopyThatIsAGuiProgram_SuppressesTheLaterConsoleCopy()
  {
    string first = NewDirectory();
    string second = NewDirectory();
    WriteExecutable(first, "calc.exe", GuiSubsystem);
    WriteExecutable(second, "calc.exe", ConsoleSubsystem);

    Assert.Empty(PathToolDiscovery.Scan([first, second]).Tools);
  }

  [Fact]
  public void NameMatching_IsCaseInsensitive_AndKeepsTheFirstSpelling()
  {
    string first = NewDirectory();
    string second = NewDirectory();
    WriteExecutable(first, "RG.exe", ConsoleSubsystem);
    WriteExecutable(second, "rg.exe", ConsoleSubsystem);

    PathTool tool = Assert.Single(PathToolDiscovery.Scan([first, second]).Tools);

    Assert.Equal("RG", tool.Name);
    Assert.Equal(first, tool.SourceDirectory);
  }

  // ---- Directories: a bad entry costs its own contribution, never the scan ----

  [Fact]
  public void MissingDirectory_ContributesNothing_AndDoesNotAbortTheScan()
  {
    string present = NewDirectory();
    WriteExecutable(present, "rg.exe", ConsoleSubsystem);
    string absent = Path.Combine(TestTempRoot.Root, "never-created-" + Guid.NewGuid().ToString("N"));

    PathToolScan scan = PathToolDiscovery.Scan([absent, present]);

    Assert.Equal(1, scan.MissingDirectoryCount);
    PathTool tool = Assert.Single(scan.Tools);
    Assert.Equal("rg", tool.Name);
  }

  [Fact]
  public void EmptyDirectoryList_YieldsAnEmptyResult()
  {
    PathToolScan scan = PathToolDiscovery.Scan([]);

    Assert.Empty(scan.Directories);
    Assert.Empty(scan.Tools);
    Assert.Equal(0, scan.MissingDirectoryCount);
  }

  [Fact]
  public void BlankDirectoryEntry_IsSkippedWithoutCountingAsMissing()
  {
    string present = NewDirectory();
    WriteExecutable(present, "rg.exe", ConsoleSubsystem);

    PathToolScan scan = PathToolDiscovery.Scan(["", "   ", present]);

    Assert.Equal(0, scan.MissingDirectoryCount);
    _ = Assert.Single(scan.Tools);
  }

  [Fact]
  public void DirectoryHoldingNoTools_ContributesNoGroup()
  {
    string empty = NewDirectory();

    PathToolScan scan = PathToolDiscovery.Scan([empty]);

    Assert.Empty(scan.Directories);
    Assert.Equal(0, scan.MissingDirectoryCount);
  }

  // ---- Grouping and ordering ----

  [Fact]
  public void Tools_AreGroupedBySourceDirectory_InTheSuppliedOrder()
  {
    string first = NewDirectory();
    string second = NewDirectory();
    WriteExecutable(first, "rg.exe", ConsoleSubsystem);
    File.WriteAllText(Path.Combine(first, "npm.cmd"), "@echo off\r\n");
    WriteExecutable(second, "jq.exe", ConsoleSubsystem);

    PathToolScan scan = PathToolDiscovery.Scan([first, second]);

    Assert.Equal(2, scan.Directories.Count);
    Assert.Equal(first, scan.Directories[0].Directory);
    Assert.Equal(["npm", "rg"], scan.Directories[0].Tools.Select(tool => tool.Name));
    Assert.Equal(second, scan.Directories[1].Directory);
    Assert.Equal("jq", Assert.Single(scan.Directories[1].Tools).Name);
  }

  [Fact]
  public void NothingIsFilteredByNameFolderOrSize()
  {
    string directory = Path.Combine(NewDirectory(), "System32");
    _ = Directory.CreateDirectory(directory);
    for (int index = 0; index < 40; index++)
    {
      WriteExecutable(directory, "tool" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ".exe", ConsoleSubsystem);
    }

    Assert.Equal(40, PathToolDiscovery.Scan([directory]).Tools.Count());
  }

  // ---- Resolve: does a name run, and which file runs it ----

  [Fact]
  public void Resolve_FindsACmdShimByBareName()
  {
    string directory = NewDirectory();
    File.WriteAllText(Path.Combine(directory, "npm.cmd"), "@echo off\r\n");

    PathToolResolution resolution = Assert.Single(PathToolDiscovery.Resolve(["npm"], [directory]));

    Assert.True(resolution.IsResolved);
    Assert.Equal("npm", resolution.Name);
    Assert.Equal(PathToolKind.Script, resolution.Kind);
    Assert.Equal(Path.Combine(directory, "npm.cmd"), resolution.FullPath);
  }

  [Fact]
  public void Resolve_FindsAnExecutableByBareName()
  {
    string directory = NewDirectory();
    WriteExecutable(directory, "node.exe", ConsoleSubsystem);

    PathToolResolution resolution = Assert.Single(PathToolDiscovery.Resolve(["node"], [directory]));

    Assert.Equal(PathToolKind.ConsoleProgram, resolution.Kind);
    Assert.Equal(Path.Combine(directory, "node.exe"), resolution.FullPath);
  }

  [Fact]
  public void Resolve_AcceptsANameThatCarriesItsExtension()
  {
    string directory = NewDirectory();
    WriteExecutable(directory, "rg.exe", ConsoleSubsystem);

    PathToolResolution resolution = Assert.Single(PathToolDiscovery.Resolve(["rg.exe"], [directory]));

    Assert.True(resolution.IsResolved);
    Assert.Equal(Path.Combine(directory, "rg.exe"), resolution.FullPath);
  }

  [Fact]
  public void Resolve_IsCaseInsensitive()
  {
    string directory = NewDirectory();
    WriteExecutable(directory, "RG.EXE", ConsoleSubsystem);

    Assert.True(Assert.Single(PathToolDiscovery.Resolve(["rg"], [directory])).IsResolved);
  }

  [Fact]
  public void Resolve_UnknownName_IsUnresolved()
  {
    string directory = NewDirectory();

    PathToolResolution resolution = Assert.Single(PathToolDiscovery.Resolve(["nosuchtool"], [directory]));

    Assert.False(resolution.IsResolved);
    Assert.Null(resolution.Kind);
    Assert.Null(resolution.FullPath);
  }

  [Fact]
  public void Resolve_FirstMatchBeingAGuiProgram_LeavesTheNameUnresolved()
  {
    string first = NewDirectory();
    string second = NewDirectory();
    WriteExecutable(first, "calc.exe", GuiSubsystem);
    WriteExecutable(second, "calc.exe", ConsoleSubsystem);

    Assert.False(Assert.Single(PathToolDiscovery.Resolve(["calc"], [first, second])).IsResolved);
  }

  [Fact]
  public void Resolve_EarliestDirectoryWins()
  {
    string first = NewDirectory();
    string second = NewDirectory();
    File.WriteAllText(Path.Combine(first, "npm.cmd"), "@echo off\r\n");
    WriteExecutable(second, "npm.exe", ConsoleSubsystem);

    PathToolResolution resolution = Assert.Single(PathToolDiscovery.Resolve(["npm"], [first, second]));

    Assert.Equal(Path.Combine(first, "npm.cmd"), resolution.FullPath);
  }

  [Fact]
  public void Resolve_ReturnsOneResultPerNameInTheOrderGiven()
  {
    string directory = NewDirectory();
    WriteExecutable(directory, "rg.exe", ConsoleSubsystem);

    IReadOnlyList<PathToolResolution> results = PathToolDiscovery.Resolve(["nosuchtool", "rg"], [directory]);

    Assert.Equal(2, results.Count);
    Assert.Equal("nosuchtool", results[0].Name);
    Assert.False(results[0].IsResolved);
    Assert.Equal("rg", results[1].Name);
    Assert.True(results[1].IsResolved);
  }

  [Fact]
  public void Resolve_WithNoDirectories_ResolvesNothing() =>
      Assert.False(Assert.Single(PathToolDiscovery.Resolve(["rg"], [])).IsResolved);

  [Fact]
  public void Resolve_AgreesWithScanOnWhatIsListed()
  {
    string directory = NewDirectory();
    WriteExecutable(directory, "rg.exe", ConsoleSubsystem);
    WriteExecutable(directory, "calc.exe", GuiSubsystem);
    File.WriteAllText(Path.Combine(directory, "npm.cmd"), "@echo off\r\n");

    string[] listed = [.. PathToolDiscovery.Scan([directory]).Tools.Select(tool => tool.Name)];
    IReadOnlyList<PathToolResolution> resolved = PathToolDiscovery.Resolve(listed, [directory]);

    Assert.Equal(["npm", "rg"], listed);
    Assert.All(resolved, resolution => Assert.True(resolution.IsResolved));
    Assert.False(Assert.Single(PathToolDiscovery.Resolve(["calc"], [directory])).IsResolved);
  }

  // ---- Fixtures ----

  private static string NewDirectory() => TestTempRoot.CreateDirectory("path-tools").FullName;

  /// <summary>Writes the smallest PE image whose optional header names
  ///     <paramref name="subsystem"/>: a DOS header pointing at a signature, a 20-byte COFF
  ///     header, and an optional header long enough to hold the Subsystem field.</summary>
  private static void WriteExecutable(string directory, string fileName, ushort subsystem) =>
      WriteImage(Path.Combine(directory, fileName), subsystem);

  private static void WriteImage(string path, ushort subsystem)
  {
    byte[] image = new byte[SignatureOffset + SubsystemFromSignature + sizeof(ushort)];
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(PeHeaderOffsetField), SignatureOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SignatureOffset), 0x00004550);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(SignatureOffset + SubsystemFromSignature), subsystem);
    File.WriteAllBytes(path, image);
  }
}
