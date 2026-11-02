// ERP-work pickup wiring: the pickup service posts the explicit
// /agent/pickup shape with the company code, and a blank company code is
// refused before any POST. Temp ERP and cache only — never
// company-data numbers.
using System.Net;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval.Ui;

public sealed class PickupWiringTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public int CallCount;
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        public string Json = """{"touched":[],"count":0}""";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (SimulationUiService Service, StubHandler Http) CreateService(string json)
    {
        var dir = UiTestSupport.NewDir("gate-ui-pickup-svc");
        Directory.CreateDirectory(Path.Combine(dir, "dock"));
        var stub = new StubHandler { Json = json };
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://localhost/") };
        var service = new SimulationUiService(
            http, new InMemoryErpAdapter(), new CaseCacheStore(Path.Combine(dir, "case-cache")),
            new DockPhotoService(Path.Combine(dir, "dock"), Path.Combine(dir, "vision")),
            UiTestLogger<SimulationUiService>.Instance);
        return (service, stub);
    }

    [Fact]
    public async Task PickupServicePostsExplicitEndpointShape()
    {
        var (service, stub) = CreateService("""{"touched":["CASE-A","CASE-B"],"count":2}""");

        var outcome = await service.PickUpErpWorkAsync("LE-T");

        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Equal(new[] { "CASE-A", "CASE-B" }, outcome.CaseIds);
        Assert.Equal(1, stub.CallCount);
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal(SimulationUiService.PickupPath, stub.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("LE-T", stub.LastBody, StringComparison.Ordinal);
        Assert.Contains("entityCode", stub.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PickupServiceRefusesBlankEntity()
    {
        var (service, stub) = CreateService("""{"touched":[],"count":0}""");

        var outcome = await service.PickUpErpWorkAsync("  ");

        Assert.False(outcome.Ok);
        Assert.Equal(0, stub.CallCount);
    }
}
