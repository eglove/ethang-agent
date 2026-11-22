using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Reference resolution on the submit path: a message's <c>@</c> tokens resolve
///     through the resolver before any turn starts, the transcript bubble keeps exactly what
///     the user typed, a referenced file is NAMED to the model rather than read for it, a
///     reference that resolves to nothing posts a notice and the message still sends, and a
///     slash name outside the closed command registry falls through as a plain message.</summary>
public class ReferenceSubmitTests
{
  private static SkillDefinition Skill(string name) => new(
      name, "Does " + name + " things", "Body of " + name, 1, SkillSource.BuiltIn);

  private sealed class FakeCatalog(params SkillDefinition[] skills) : ISkillCatalog
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([.. skills]));

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<SkillDefinition>(new DomainError("SkillNotFound", name)));
  }

  /// <summary>Counts reads so a test can prove the submit path never touched the file system
  ///     for a reference.</summary>
  private sealed class CountingFiles(IFileSystemAccess inner) : IFileSystemAccess
  {
    public int ReadCount { get; private set; }

    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
        CancellationToken ct = default)
    {
      ReadCount++;
      return inner.ReadLinesAsync(path, startLine, endLine, ct);
    }

    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default)
      => inner.ReadBytesAsync(path, ct);
  }

  private sealed class Harness(AgentSessionViewModel vm, List<string> turns, string root,
      DirectFileSystemAccess files, CountingFiles counter) : IDisposable
  {
    public AgentSessionViewModel Vm { get; } = vm;

    public List<string> Turns { get; } = turns;

    public string Root { get; } = root;

    public CountingFiles Files { get; } = counter;

    public void Dispose()
    {
      files.Dispose();
#pragma warning disable CA1031 // Named decision: temp-dir cleanup is best effort.
      try
      {
        Directory.Delete(Root, recursive: true);
      }
      catch (IOException)
      {
        // best effort: a still-draining reader may pin the directory
      }
#pragma warning restore CA1031
    }
  }

  /// <summary>Builds a session over a temp workspace whose files are the given ones.</summary>
  private static Harness Build(IReadOnlyDictionary<string, string[]> files, params SkillDefinition[] skills)
  {
    string root = TestTempRoot.CreateDirectory("ethang-refsubmit").FullName;
    foreach ((string name, string[] content) in files)
    {
      string path = Path.Combine(root, name);
      _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllLines(path, content);
    }

    List<string> turns = [];
    DirectFileSystemAccess fileAccess = new(new EditorConfigTextConventionPolicy(root));
    CountingFiles counter = new(fileAccess);
    // The grant set is what a quoted path fills and what the tools consult, exactly as the
    // production container wires it.
    SessionPathGrants grants = new();
    AgentSessionViewModel vm = new(
        (command, _, _, _) =>
        {
          turns.Add(command.Text);
          return Task.FromResult(Result.Success("ok"));
        },
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = root,
          SkillInvocation = skills.Length == 0 ? null : new SkillInvocationService(new FakeCatalog(skills)),
          References = new ReferenceResolver(
              skills.Length == 0 ? null : new FakeCatalog(skills),
              root,
              grants),
          PathGrants = grants,
        });
    return new Harness(vm, turns, root, fileAccess, counter);
  }

  [Fact]
  public async Task SkillNamesNoLongerResolveThroughSlash()
  {
    using Harness h = Build(new Dictionary<string, string[]>(), Skill("grill"));

    await h.Vm.SubmitAsync("/grill").ConfigureAwait(true);
    await h.Vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.DoesNotContain(h.Vm.Transcript.Entries, e => e is SystemMessageEntry);
    Assert.Equal(["/grill"], h.Turns);
  }

  [Fact]
  public async Task ASkillReferenceNamesTheSkillAndDoesNotInvokeIt()
  {
    // A skill reference is a pointer like a file reference: the model is told the skill exists
    // and its body stays behind skill_view, so nothing is appended beneath the bubble.
    using Harness h = Build(new Dictionary<string, string[]>(), Skill("grill"));

    await h.Vm.SubmitAsync("use @grill on this").ConfigureAwait(true);
    await h.Vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.DoesNotContain(h.Vm.Transcript.Entries, e => e is ToolResultEntry);
    Assert.DoesNotContain(h.Vm.Transcript.Entries, e => e is SystemMessageEntry);
    string sent = Assert.Single(h.Turns);
    Assert.StartsWith("use @grill on this", sent, StringComparison.Ordinal);
    Assert.Contains("skill 'grill'", sent, StringComparison.Ordinal);
    Assert.DoesNotContain("Body of grill", sent, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownSlashFallsThroughAsAPlainMessage()
  {
    using Harness h = Build(new Dictionary<string, string[]>());

    await h.Vm.SubmitAsync("/zzz hello").ConfigureAwait(true);
    await h.Vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.Contains(h.Vm.Transcript.Entries, e => e is UserMessageEntry u && u.Text == "/zzz hello");
    Assert.Equal(["/zzz hello"], h.Turns);
  }

  [Fact]
  public async Task AReferenceEscapingTheWorkspacePostsANoticeAndTheMessageStillSends()
  {
    // A reference that resolves to nothing must not cost the user the message: the notice
    // says what was left out and the turn runs with the text exactly as typed.
    using Harness h = Build(new Dictionary<string, string[]>());

    await h.Vm.SubmitAsync("read @../outside.cs please").ConfigureAwait(true);
    await h.Vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.Contains(h.Vm.Transcript.Entries,
        e => e is NoticeEntry n && n.Text.Contains("ReferenceOutsideWorkspace", StringComparison.Ordinal));
    Assert.Contains(h.Vm.Transcript.Entries,
        e => e is UserMessageEntry u && u.Text == "read @../outside.cs please");
    Assert.Equal(["read @../outside.cs please"], h.Turns);
  }

  [Fact]
  public async Task AQuotedExternalPathIsGrantedAndNamedButNotRead()
  {
    // The quoted form is what grants the path, and the grant is what lets the read tools reach
    // a file the workspace resolver refuses. The harness still does not read it.
    string external = TestTempRoot.CreateDirectory("ethang-refexternal").FullName;
    string file = Path.Combine(external, "View encrypted message.pdf.txt");
    await File.WriteAllLinesAsync(file, ["secret", "payload"], TestContext.Current.CancellationToken);
    try
    {
      using Harness h = Build(new Dictionary<string, string[]>());

      await h.Vm.SubmitAsync($"@\"{file}\" please").ConfigureAwait(true);
      await h.Vm.WaitForTurnAsync().ConfigureAwait(true);

      Assert.DoesNotContain(h.Vm.Transcript.Entries,
          e => e is NoticeEntry n && n.Text.Contains("Reference", StringComparison.Ordinal));
      Assert.DoesNotContain(h.Vm.Transcript.Entries, e => e is ToolResultEntry);
      Assert.Equal(0, h.Files.ReadCount);
      string sent = Assert.Single(h.Turns);
      Assert.Contains("[references]", sent, StringComparison.Ordinal);
      Assert.Contains("granted read-only", sent, StringComparison.Ordinal);
    }
    finally
    {
      Directory.Delete(external, recursive: true);
    }
  }

  [Fact]
  public async Task AnUnquotedExternalPathStaysOutOfReach()
  {
    // The same path without its quotes grants nothing: naming a file in prose is not a
    // statement that the session may open it.
    string external = TestTempRoot.CreateDirectory("ethang-refexternal2").FullName;
    string file = Path.Combine(external, "notes.txt");
    await File.WriteAllLinesAsync(file, ["secret"], TestContext.Current.CancellationToken);
    try
    {
      using Harness h = Build(new Dictionary<string, string[]>());

      await h.Vm.SubmitAsync($"{file} please").ConfigureAwait(true);
      await h.Vm.WaitForTurnAsync().ConfigureAwait(true);

      _ = Assert.Single(h.Turns);
      Assert.Equal(0, h.Files.ReadCount);
    }
    finally
    {
      Directory.Delete(external, recursive: true);
    }
  }

  [Fact]
  public void TheReadoutCountsReferencesWithoutResolvingThem()
  {
    // The readout is a count of what the message names. It never resolves or reads, so every
    // keystroke may ask it and the file system is never touched.
    using Harness h = Build(new Dictionary<string, string[]> { ["notes.txt"] = ["alpha", "beta"] });

    h.Vm.UpdateReferenceCount("check @notes.txt");
    Assert.Equal("1 reference", h.Vm.ReferenceCountLabel);

    h.Vm.UpdateReferenceCount("check @notes.txt and @other.txt");
    Assert.Equal("2 references", h.Vm.ReferenceCountLabel);
    Assert.Equal(0, h.Files.ReadCount);
  }

  [Fact]
  public void PlainProseShowsNoReadout()
  {
    using Harness h = Build(new Dictionary<string, string[]> { ["notes.txt"] = ["alpha"] });

    h.Vm.UpdateReferenceCount("just some words");
    Assert.Equal(string.Empty, h.Vm.ReferenceCountLabel);
    Assert.Equal(0, h.Files.ReadCount);
  }

  [Fact]
  public async Task AReferencedFileIsNamedToTheModelAndNotRead()
  {
    // The bubble shows what the user typed; the model receives the message plus the annotation
    // naming the path, and decides for itself whether to read it.
    using Harness h = Build(new Dictionary<string, string[]> { ["notes.txt"] = ["alpha", "beta"] });

    await h.Vm.SubmitAsync("check @notes.txt").ConfigureAwait(true);
    await h.Vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.Contains(h.Vm.Transcript.Entries, e => e is UserMessageEntry u && u.Text == "check @notes.txt");
    Assert.DoesNotContain(h.Vm.Transcript.Entries, e => e is ToolResultEntry);
    Assert.Equal(0, h.Files.ReadCount);
    string sent = Assert.Single(h.Turns);
    Assert.StartsWith("check @notes.txt", sent, StringComparison.Ordinal);
    Assert.Contains("@notes.txt", sent, StringComparison.Ordinal);
    Assert.DoesNotContain("1→ alpha", sent, StringComparison.Ordinal);
  }
}
