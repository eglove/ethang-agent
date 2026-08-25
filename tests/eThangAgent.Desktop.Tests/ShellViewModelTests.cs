using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Shell-level behavior of the main window: menu bar, tab lifecycle, and the
///     empty state. Session creation is faked at the factory seam — the factory's own
///     composition is covered in eThangAgent.Composition.Tests.</summary>
public class ShellViewModelTests
{
    private static MainViewModel CreateShell(Func<string, AgentSession>? factory = null)
    {
        Func<string, Task<Result<AgentSession>>> create = root =>
        {
            if (factory is null)
                return Task.FromResult(Result<AgentSession>.Failure(new Error("NoFactory", "no session factory configured")));
            var session = factory(root);
            return Task.FromResult(Result<AgentSession>.Success(session));
        };
        return new MainViewModel(create);
    }

    private static AgentSession FakeSession(string root)
    {
        // A session whose container is never disposed (no ServiceProvider) — tests that
        // close tabs must not touch Services. Build via a throwaway ServiceCollection so
        // DisposeAsync stays legal.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .BuildServiceProvider();
        return new AgentSession(
            services,
            eThangAgent.AgentDomain.AgentId.NewId(),
            new eThangAgent.ConversationDomain.Conversation(),
            Handler: null!,
            Lifecycle: new eThangAgent.Composition.RootSessionLifecycle(new TestFixtures.StubStore()),
            Model: eThangAgent.ModelDomain.ModelConfig.Create("test/model", 128, 0.1f).Value!,
            WorkspaceRoot: root,
            ClarifyChannel: null!,
            Inbox: new eThangAgent.AgentDomain.AgentInbox(),
            ChildRuntime: new TestFixtures.StubAgentRuntime());
    }

    [Fact]
    public void OpenAgentCommand_Raises_Picker_Request()
    {
        var vm = CreateShell();
        var raised = false;
        vm.OpenAgentRequested += (_, _) => raised = true;

        vm.OpenAgentCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public async Task OpenAgent_Creates_Tab_Selects_It_And_Reports_HasTabs()
    {
        var vm = CreateShell(root => FakeSession(root));

        var opened = await vm.OpenAgentAsync(@"C:\work\alpha");

        Assert.True(opened.IsSuccess);
        Assert.Single(vm.Tabs);
        Assert.Equal(vm.Tabs[0], vm.SelectedTab);
        Assert.True(vm.HasTabs);
        Assert.Equal("alpha", opened.Value!.Title);
    }

    [Fact]
    public async Task Opening_Same_Directory_Twice_Selects_Existing_Tab()
    {
        var vm = CreateShell(root => FakeSession(root));
        await vm.OpenAgentAsync(@"C:\work\alpha");

        var second = await vm.OpenAgentAsync(@"C:\work\alpha");

        Assert.Single(vm.Tabs);
        Assert.Equal(vm.Tabs[0], vm.SelectedTab);
        Assert.Equal(vm.Tabs[0], second.Value);
    }

    [Fact]
    public async Task Opening_A_Second_Directory_Adds_Another_Tab()
    {
        var vm = CreateShell(root => FakeSession(root));
        await vm.OpenAgentAsync(@"C:\work\alpha");
        await vm.OpenAgentAsync(@"C:\work\beta");

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Equal("beta", vm.SelectedTab!.Title); // newest tab selected
    }

    [Fact]
    public async Task CloseTab_Removes_Tab_And_Falls_Back_Selection_To_Last()
    {
        var vm = CreateShell(root => FakeSession(root));
        var alpha = (await vm.OpenAgentAsync(@"C:\work\alpha")).Value!;
        var beta = (await vm.OpenAgentAsync(@"C:\work\beta")).Value!;
        Assert.Equal(beta, vm.SelectedTab);

        await vm.CloseTabAsync(beta);

        Assert.DoesNotContain(beta, vm.Tabs);
        Assert.Equal(alpha, vm.SelectedTab);
        Assert.True(vm.HasTabs);
    }

    [Fact]
    public async Task Closing_Last_Tab_Returns_To_Empty_Shell()
    {
        var vm = CreateShell(root => FakeSession(root));
        var alpha = (await vm.OpenAgentAsync(@"C:\work\alpha")).Value!;

        await vm.CloseTabAsync(alpha);

        Assert.Empty(vm.Tabs);
        Assert.False(vm.HasTabs);
        Assert.Null(vm.SelectedTab);
    }

    [Fact]
    public async Task OpenAgent_Failure_Surfaces_Structured_Error_And_No_Tab()
    {
        var vm = CreateShell(); // factory fails every request

        var opened = await vm.OpenAgentAsync(@"C:\work\gamma");

        Assert.False(opened.IsSuccess);
        Assert.Equal("NoFactory", opened.Error!.Code);
        Assert.Empty(vm.Tabs);
    }
}
