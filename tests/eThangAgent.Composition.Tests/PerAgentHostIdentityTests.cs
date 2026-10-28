using eThangAgent.AgentDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>One process per agent (issue #305 criteria 1 and 9). The isolation the direction
///     depends on: a hung agent that shares a process with three others cannot be killed
///     without killing them, and a crashed one cannot be resumed without taking the others down.
///     <para>These tests pin the IDENTITY that makes it true — every agent's host is keyed by the
///     agent's own id, so two agents cannot share a pipe, a scratch folder, or (through
///     RemoteHostSupervisor's job object) a kill boundary. That is what makes criterion 9
///     ('killing one agent's host leaves every other agent running') structural rather than
///     hopeful: there is no shared object to take a sibling down with.</para></summary>
public class PerAgentHostIdentityTests
{
  [Fact]
  public void TwoAgents_GetDistinctPipeNames()
  {
    // The pipe name IS the host's address. Two agents sharing one would be one host with two
    // owners — the in-host sharing this issue retires.
    string first = RemoteHostSupervisor.PipeNameForConversation(Guid.NewGuid().ToString());
    string second = RemoteHostSupervisor.PipeNameForConversation(Guid.NewGuid().ToString());

    Assert.NotEqual(first, second);
  }

  [Fact]
  public void TwoAgents_GetDistinctScratchFolders()
  {
    // The scratch folder is the host's working directory and the home of its settings JSON.
    // Sharing it would let one agent's launch overwrite another's settings file.
    string first = RemoteHostSupervisor.ScratchFolderForConversation(Guid.NewGuid().ToString());
    string second = RemoteHostSupervisor.ScratchFolderForConversation(Guid.NewGuid().ToString());

    Assert.NotEqual(first, second);
  }

  [Fact]
  public void SameAgent_KeepsItsIdentityAcrossRestarts()
  {
    // Stable derivations are what let a relaunching app FIND the host it already started rather
    // than spawning a second one for the same agent.
    string agent = Guid.NewGuid().ToString();

    Assert.Equal(
        RemoteHostSupervisor.PipeNameForConversation(agent),
        RemoteHostSupervisor.PipeNameForConversation(agent));
    Assert.Equal(
        RemoteHostSupervisor.ScratchFolderForConversation(agent),
        RemoteHostSupervisor.ScratchFolderForConversation(agent));
  }

  [Fact]
  public void ScratchFolder_IsFilesystemSafe()
  {
    // An agent id is a GUID today, but the derivation hashes whatever it is given — ids are often
    // absolute paths elsewhere in this codebase, and a raw path segment would silently discard
    // every earlier Path.Combine segment.
    string folder = RemoteHostSupervisor.ScratchFolderForConversation(@"C:\some\workspace\path");

    Assert.DoesNotContain('\\', folder);
    Assert.DoesNotContain(':', folder);
  }

  [Fact]
  public void Capacity_AdmitsTheMeasuredPeak()
  {
    // Criterion 4's own words: 'a run with at least 62 concurrent hosts must be admitted'.
    // The gate is the host boundary now, so it must admit the peak the issue measured.
    HostAdmissionGate gate = new(HostCapacity.ForMachine(34_101_985_280L));

    for (int i = 0; i < 62; i++)
    {
      Assert.True(gate.Request(Guid.NewGuid()).IsAdmitted);
    }

    Assert.Equal(62, gate.Live);
  }
}
