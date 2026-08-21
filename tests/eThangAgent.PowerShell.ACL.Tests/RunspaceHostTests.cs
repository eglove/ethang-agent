using System.Management.Automation.Runspaces;
using eThangAgent.PowerShell.ACL;

namespace eThangAgent.PowerShell.ACL.Tests;

public class RunspaceHostTests
{
    [Fact]
    public void CreateOpen_ReturnsOpenedRunspace()
    {
        using var runspace = RunspaceHost.CreateOpen();

        Assert.Equal(RunspaceState.Opened, runspace.RunspaceStateInfo.State);
    }
}
