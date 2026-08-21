using System.Management.Automation.Runspaces;

namespace eThangAgent.PowerShell.ACL;

/// <summary>Single creation point for plain in-process runspaces.</summary>
public static class RunspaceHost
{
    public static Runspace CreateOpen()
    {
        var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        runspace.Open();
        return runspace;
    }
}
