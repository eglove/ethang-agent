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
///     through the injector before any turn starts, the transcript bubble keeps exactly what
///     the user typed, each resolved file's read output lands beneath that bubble, a missing
///     reference refuses the send and keeps the input, and a slash name outside the closed
///     command registry falls through as a plain message.</summary>
public class ReferenceSubmitTests
{
  private static SkillDefinition Skill(string name) => new(
      name, "Does " + name + " things", "Body of " + name, 1, SkillSource.BuiltIn,
      null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, null);

  private sealed class FakeCatalog(params SkillDefinition[] skills) : ISkillCatalog
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([.. skills]));

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<SkillDefinition>(new DomainError("SkillNotFound", name)));
  }

  /// <summary>The built session plus the temp workspace it reads; disposing it releases the
  ///     file-system access and removes the directory.</summary>
  /// <summary>Counts reads so a test can prove the cost estimate did (or did not) touch the
  ///     file system for a given input.</summary>
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

  /// <summary>Builds a session over a temp workspace whose files are the given ones. The
  ///     injector reads the same real file system the read tool does.</summary>
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
    DirectFileSystemAccess fileAccess = new(new EditorConfigLineEndingPolicy(root));
    CountingFiles counter = new(fileAccess);
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
          References = new ReferenceInjector(
              counter,
              skills.Length == 0 ? null : new SkillInvocationService(new FakeCatalog(skills)),
              root),
        });
    return new Harness(vm, turns, root, fileAccess, counter);
  }

  [Fact]
  public async Task SkillNamesNoLongerResolveThroughSlash()
  {
    using Harness h = Build(new Dictionary<string, string[]>(), Skill("grill"));

    await h.Vm.SubmitAsync("/grill").ConfigureAwait(true);
    await h.Vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.DoesNotContain(h.Vm.Transcript.Entries,
        e => e is SystemMessageEntry s && s.Text.Contains("[skill invoked", StringComparison.Ordinal));
    Assert.Equal(["/grill"], h.Turns);
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
  public async Task MissingReferenceKeepsTheInputAndPostsANotice()
  {
    using Harness h = Build(new Dictionary<string, string[]>());

    await h.Vm.SubmitAsync("read @missing.cs please").ConfigureAwait(true);
    await h.Vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.DoesNotContain(h.Vm.Transcript.Entries, e => e is UserMessageEntry);
    Assert.Contains(h.Vm.Transcript.Entries,
        e => e is NoticeEntry n && n.Text.Contains("ReferenceNotFound", StringComparison.Ordinal));
    Assert.Equal("read @missing.cs please", h.Vm.PendingRefusedInput);
    Assert.Empty(h.Turns);
  }

  [Fact]
  public async Task TheCostEstimateDoesNotReReadForUnchangedReferences()
  {
    // Typing prose around a reference must not re-read the file: the estimate keys on the
    // reference NAMES, so an unchanged set reuses the last resolution.
    using Harness h = Build(new Dictionary<string, string[]> { ["notes.txt"] = ["alpha", "beta"] });

    await h.Vm.UpdateReferenceCostAsync("check @notes.txt").ConfigureAwait(true);
    int reads = h.Files.ReadCount;
    Assert.True(reads > 0, "the first estimate must resolve the reference");

    await h.Vm.UpdateReferenceCostAsync("check @notes.txt please").ConfigureAwait(true);
    await h.Vm.UpdateReferenceCostAsync("check @notes.txt please now").ConfigureAwait(true);
    Assert.Equal(reads, h.Files.ReadCount);
  }

  [Fact]
  public async Task PlainProseNeverResolvesAReference()
  {
    using Harness h = Build(new Dictionary<string, string[]> { ["notes.txt"] = ["alpha"] });

    await h.Vm.UpdateReferenceCostAsync("just some words").ConfigureAwait(true);
    Assert.Equal(0, h.Files.ReadCount);
    Assert.Equal(string.Empty, h.Vm.ReferenceCostLabel);
  }

  [Fact]
  public async Task ReferenceBlocksAppearBeneathTheUserMessage()
  {
    using Harness h = Build(new Dictionary<string, string[]> { ["notes.txt"] = ["alpha", "beta"] });

    await h.Vm.SubmitAsync("check @notes.txt").ConfigureAwait(true);
    await h.Vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.Contains(h.Vm.Transcript.Entries, e => e is UserMessageEntry u && u.Text == "check @notes.txt");
    Assert.Contains(h.Vm.Transcript.Entries,
        e => e is ToolResultEntry t && t.FullContent.Contains("1→ ", StringComparison.Ordinal));
    Assert.Equal(["check @notes.txt"], h.Turns);
    Assert.Null(h.Vm.PendingRefusedInput);
  }
}
