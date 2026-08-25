using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The session factory is the multi-workspace seam: each created session
///     must carry its own workspace identity and path resolver rooted at the chosen
///     directory while sharing the one app database.</summary>
public class AgentSessionFactoryTests
{
    private sealed class StubChannel : IClarifyChannel
    {
        public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Success("1"));
    }

    private static (AgentSessionFactory Factory, string DbPath) CreateFactory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ethang-factory-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
        var settings = new AgentSettings("sk-or-test", new Uri("https://openrouter.test"),
            new SubAgentOptions(null, TimeSpan.FromSeconds(300), 2));
        return (new AgentSessionFactory(settings, settings.ApiKey!,
            ModelConfig.Create("test/model", 512, 0.5f).Value!), dbPath);
    }

    [Fact]
    public async Task CreateAsync_Builds_Isolated_Sessions_Per_Workspace()
    {
        var (factory, db) = CreateFactory();
        try
        {
            var dirA = Directory.CreateTempSubdirectory("ethang-ws-a");
            var dirB = Directory.CreateTempSubdirectory("ethang-ws-b");
            try
            {
                var a = await factory.CreateAsync(dirA.FullName, new StubChannel());
                var b = await factory.CreateAsync(dirB.FullName, new StubChannel());

                Assert.True(a.IsSuccess);
                Assert.True(b.IsSuccess);

                // Distinct roots, identities, resolvers, conversations — nothing shared.
                Assert.NotEqual(a.Value!.RootId, b.Value!.RootId);
                Assert.NotSame(a.Value.Conversation, b.Value.Conversation);
                Assert.Equal(dirA.FullName, a.Value.WorkspaceRoot);
                Assert.Equal(dirB.FullName, b.Value.WorkspaceRoot);

                // Each session's workspace context carries ITS OWN root path as identity.
                var ctxA = a.Value.Services.GetRequiredService<IWorkspaceContext>();
                var ctxB = b.Value.Services.GetRequiredService<IWorkspaceContext>();
                Assert.Equal(dirA.FullName, ctxA.WorkspaceId);
                Assert.Equal(dirB.FullName, ctxB.WorkspaceId);

                // Path resolution is jailed to each session's own root.
                var resolver = a.Value.Services.GetRequiredService<IPathResolver>();
                Assert.True(resolver.Resolve("file.txt").IsSuccess);
                Assert.True(
                    !resolver.Resolve(Path.Combine(dirB.FullName, "escape.txt")).IsSuccess);

                // The exec engine resolves Workspace per execution against the session's
                // own identity — never a process-global cwd captured at construction.
                var engine = a.Value.Services.GetRequiredService<IExecEngine>();
                var run = await engine.ExecuteAsync(new ExecProgram("return Workspace;"));
                Assert.Contains(dirA.FullName, run.Output, StringComparison.OrdinalIgnoreCase);
            }
            finally { dirA.Delete(true); dirB.Delete(true); }
        }
        finally { Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null); try { File.Delete(db); } catch { } }
    }

    [Fact]
    public async Task CreateAsync_Rejects_Missing_Directory_With_Structured_Error()
    {
        var (factory, db) = CreateFactory();
        try
        {
            var missing = Path.Combine(Path.GetTempPath(), $"ethang-missing-{Guid.NewGuid():N}");
            var result = await factory.CreateAsync(missing, new StubChannel());

            Assert.False(result.IsSuccess);
            Assert.Equal("WorkspaceNotFound", result.Error!.Code);
        }
        finally { Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null); try { File.Delete(db); } catch { } }
    }
}
