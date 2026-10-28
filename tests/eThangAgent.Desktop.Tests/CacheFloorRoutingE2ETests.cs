using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

[Collection("Desktop E2E")]
public class CacheFloorRoutingE2ETests
{
  [Fact]
  public async Task CacheFloor_OrderAndReportedChargeReachTheSessionLedger()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();
    host.Mock.EndpointsResponse = /*lang=json,strict*/ "{\"data\":{\"endpoints\":[{\"provider_name\":\"Expensive\",\"tag\":\"expensive\",\"context_length\":2000000,\"pricing\":{\"prompt\":\"0.000002\",\"completion\":\"0.000004\",\"input_cache_read\":\"0.000002\"}},{\"provider_name\":\"Cheap\",\"tag\":\"cheap/flex\",\"context_length\":2000000,\"pricing\":{\"prompt\":\"0.000001\",\"completion\":\"0.000002\",\"input_cache_read\":\"0.0000001\"}}]}}";
    _ = await host.Services.GetRequiredService<IModelCatalog>().RefreshAsync(TestContext.Current.CancellationToken);
    await host.Shell.ApplyModelChoiceAsync(E2E.SessionModel, ModelVariant.CacheFloor);
    _ = host.Mock.Returns(/*lang=json,strict*/ "{\"status\":\"completed\",\"service_tier\":\"default\",\"usage\":{\"input_tokens\":100,\"output_tokens\":10,\"cost\":0.125},\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}]}");
    await host.Vm.RunTurnAsync("test routing");

    using JsonDocument request = JsonDocument.Parse(Assert.Single(host.Mock.RequestBodies));
    Assert.Equal(E2E.SessionModel, request.RootElement.GetProperty("model").GetString());
    JsonElement provider = request.RootElement.GetProperty("provider");
    JsonElement order = provider.GetProperty("order");
    Assert.Equal(2, order.GetArrayLength());
    Assert.Equal("cheap/flex", order[0].GetString());
    Assert.Equal("expensive", order[1].GetString());
    Assert.True(provider.GetProperty("allow_fallbacks").GetBoolean());
    Assert.False(provider.TryGetProperty("only", out _));

    ResultCheck(await host.Services.GetRequiredService<IUsageQueries>().SessionSummaryAsync(host.RootId.ToString(), TestContext.Current.CancellationToken));
    using SqliteConnection connection = new($"Data Source={host.DatabasePath};Mode=ReadOnly");
    await connection.OpenAsync(TestContext.Current.CancellationToken);
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT payload_json FROM agent_events WHERE type = 'usage'";
    string payload = Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    using JsonDocument recorded = JsonDocument.Parse(payload);
    Assert.Equal("default", recorded.RootElement.GetProperty("ServedServiceTier").GetString());
    Assert.Equal(0.125m, recorded.RootElement.GetProperty("ReportedCost").GetDecimal());
  }

  private static void ResultCheck(Result<UsageSummary> result)
  {
    Assert.True(result.IsSuccess);
    Assert.Equal(0.125m, result.Value.Totals.Cost);
  }

  [Fact]
  public async Task MissingCharge_StaysUnpricedInPersistedSummary()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();
    _ = host.Mock.Returns(/*lang=json,strict*/ "{\"status\":\"completed\",\"usage\":{\"input_tokens\":100,\"output_tokens\":10},\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}]}");
    await host.Vm.RunTurnAsync("test missing charge");
    Result<UsageSummary> result = await host.Services.GetRequiredService<IUsageQueries>().SessionSummaryAsync(host.RootId.ToString(), TestContext.Current.CancellationToken);
    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Totals.Cost);
    Assert.Equal(1, result.Value.Totals.UnpricedCalls);
  }

  [Fact]
  public async Task Standard_SendsNoProviderObject()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();
    await host.Shell.ApplyModelChoiceAsync(E2E.SessionModel, ModelVariant.Standard);
    _ = host.Mock.Returns(E2E.RawCompletion("ok"));
    await host.Vm.RunTurnAsync("test standard");
    using JsonDocument request = JsonDocument.Parse(Assert.Single(host.Mock.RequestBodies));
    Assert.False(request.RootElement.TryGetProperty("provider", out _));
  }
}
