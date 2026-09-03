using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>W5.3: the Desktop NoticeSink marshalling pin. Host-health and orphan-
///     repair notices are raised on background supervisory paths; the shell's
///     production sink (AttachSessionAsync) must marshal them onto the UI thread via
///     Dispatcher.Post so the transcript - UI-thread-only by contract - is never
///     mutated from the wrong side. The notice renders only after the dispatcher
///     pumps, proving the queue boundary; and a notice posted before any shell has
///     attached a sink is dropped without a fault (headless hosts).</summary>
public class NoticeSinkMarshallingTests
{
  [AvaloniaFact]
  public async Task Notice_Posted_From_A_NonUi_Thread_Renders_Only_Through_The_Dispatcher()
  {
    AgentId rootId = AgentId.NewId();
    AgentSession session = Session(rootId, new TestFixtures.ListAgentStore([Record(rootId)]));
    MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(session).ConfigureAwait(true);
    AgentSessionViewModel vm = shell.Tabs[0].ViewModel;
    int entriesBefore = vm.Transcript.Entries.Count;

    // The production raising path: a background supervisory thread posting through
    // the session sink (RemoteHostSupervisor's host-health notice shape).
    Thread poster = new(() => session.PostNotice("child host unreachable; orphan repair marked 1 row"));
    poster.Start();
    poster.Join();

    // The sink must have QUEUED the mutation (Dispatcher.Post), never mutated the
    // transcript inline on the posting thread.
    Assert.Equal(entriesBefore, vm.Transcript.Entries.Count);

    Dispatcher.UIThread.RunJobs();

    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("orphan repair", notice.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void Notice_Posted_Before_A_Sink_Is_Attached_Is_Dropped_Without_A_Fault()
  {
    AgentId rootId = AgentId.NewId();
    AgentSession session = Session(rootId, new TestFixtures.ListAgentStore([Record(rootId)]));
    Assert.Null(session.NoticeSink); // no shell attached: headless-host shape

    // Must be a silent drop - headless hosts never observe notices.
    session.PostNotice("host restarted");
  }

  private static AgentRecord Record(AgentId id) => AgentRecord.Spawned(
      id, null, 0, "test/model", null, "prompt",
      new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));

  private static AgentSession Session(AgentId rootId, IAgentStore store)
  {
    ServiceProvider services = new ServiceCollection()
        .AddSingleton(store)
        .BuildServiceProvider();
    return new AgentSession(
        services,
        rootId,
        new Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", null, 128, 0.1f, 8192).Value!,
        WorkspaceRoot: @"C:\ws\demo",
        ProviderName: "openrouter",
        ClarifyChannel: null!,
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime());
  }
}
