using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web;
using Gate.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Shared foundation wiring. The key comes from user secrets or the
// environment (MetaApi:ApiKey) — never from a file in the repo.
var spark = new SparkOptions();
builder.Configuration.GetSection("MetaApi").Bind(spark);
builder.Services.AddSingleton(spark);
builder.Services.AddSingleton<IErpAdapter, InMemoryErpAdapter>();
builder.Services.AddSingleton<IComplianceEngine, ComplianceEngine>();
builder.Services.AddSingleton<IModelClient>(sp =>
{
    IModelClient inner = string.IsNullOrWhiteSpace(spark.ApiKey)
        ? new FakeModelClient()
        : new SparkModelClient(new HttpClient(), spark);
    var cacheDir = Environment.GetEnvironmentVariable("GATE_MODEL_CACHE")
        ?? Path.Combine(Path.GetTempPath(), "gate-model-cache");
    return new CachedModelClient(inner, cacheDir);
});
builder.Services.AddSingleton<P2pAgent>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Scenario seed (additive): fills sqlite + ERP from GATE_SCENARIO on startup
// when the store is empty; no-op when no scenario file resolves.
await ScenarioEndpoints.EnsureLoadedAsync(app.Services, builder.Configuration);
app.MapScenarioReset();

app.Run();
