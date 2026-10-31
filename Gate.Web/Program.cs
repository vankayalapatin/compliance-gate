using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web;
using Gate.Web.Components;
using Gate.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Shared foundation wiring. The key comes from user secrets or the
// environment (MetaApi:ApiKey) — never from a file in the repo.
var spark = new SparkOptions();
builder.Configuration.GetSection("MetaApi").Bind(spark);
builder.Services.AddSingleton(spark);
// Real ERP: EF Core over sqlite, one context per scope. The in-memory fake
// stays for unit tests only and never backs the app.
var erpSqlite = Path.GetFullPath(ScenarioEndpoints.ErpSqlitePath(builder.Configuration));
var erpDir = Path.GetDirectoryName(erpSqlite);
if (!string.IsNullOrEmpty(erpDir))
{
    Directory.CreateDirectory(erpDir);
}
builder.Services.AddDbContext<ErpDbContext>(options => options.UseSqlite($"Data Source={erpSqlite}"));
builder.Services.AddScoped<IErpAdapter, EfCoreErpAdapter>();
builder.Services.AddSingleton<IComplianceEngine, ComplianceEngine>();
builder.Services.AddSingleton<IModelClient>(sp =>
{
    IModelClient inner = string.IsNullOrWhiteSpace(spark.ApiKey)
        ? new FakeModelClient()
        : new SparkModelClient(new HttpClient(), spark);
    return new CachedModelClient(inner, ModelCachePaths.ResolveDir());
});
builder.Services.AddScoped<P2pAgent>();
builder.Services.AddSingleton<IDockEvaluator, DockEvaluatorService>();
builder.Services.AddScoped(sp => new CaseRunner(
    sp.GetRequiredService<IErpAdapter>(),
    sp.GetRequiredService<P2pAgent>(),
    sp.GetRequiredService<IModelClient>(),
    sp.GetRequiredService<IComplianceEngine>(),
    sp.GetRequiredService<IDockEvaluator>(),
    CaseCacheStore.ResolveDir(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath),
    sp.GetRequiredService<ILogger<CaseRunner>>()));
builder.Services.AddSingleton(sp => new CaseCacheStore(
    CaseCacheStore.ResolveDir(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath)));
builder.Services.AddSingleton(sp => new MemoFileStore(
    CaseCacheStore.ResolveDir(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath)));
builder.Services.AddScoped(sp => new MemoRunner(
    sp.GetRequiredService<IErpAdapter>(),
    sp.GetRequiredService<IModelClient>(),
    sp.GetRequiredService<IComplianceEngine>(),
    CaseCacheStore.ResolveDir(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath)));
builder.Services.AddSingleton(sp => new DockPhotoService(
    DockPhotoService.ResolveImageDir(), DockPhotoService.ResolveVisionCacheDir()));
builder.Services.AddSingleton<ReplayState>();
builder.Services.AddScoped<CaseViewService>();
builder.Services.AddScoped<ActingUserState>();
builder.Services.AddScoped<DecisionOutcomeService>();
builder.Services.AddScoped<PolicyViewService>();
builder.Services.AddScoped<PolicyOutcomeService>();
builder.Services.AddScoped(sp => new AskWhyService(
    sp.GetRequiredService<P2pAgent>(),
    sp.GetRequiredService<IModelClient>(),
    sp.GetRequiredService<CaseCacheStore>(),
    sp.GetRequiredService<DockPhotoService>(),
    sp.GetRequiredService<ReplayState>(),
    sp.GetRequiredService<ILogger<AskWhyService>>(),
    ModelCachePaths.ResolveDir()));
builder.Services.AddScoped<ReplayService>();
// Simulate surface: posts the human's external inputs back to this app's own
// simulation endpoints. The generous timeout covers photo transcription
// (live model calls can take minutes). The service retries transport
// failures once with the same idempotency key, so a lost response never
// double-injects the input.
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(sp.GetRequiredService<NavigationManager>().BaseUri),
    Timeout = TimeSpan.FromMinutes(10),
});
builder.Services.AddScoped<SimulationUiService>();

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

// Scenario seed (additive): migrates the ERP database, then fills sqlite +
// ERP from GATE_SCENARIO on startup when the store is empty; no-op when no
// scenario file resolves.
using (var startup = app.Services.CreateScope())
{
    var db = startup.ServiceProvider.GetRequiredService<ErpDbContext>();
    await db.Database.MigrateAsync();
    await ScenarioEndpoints.EnsureLoadedAsync(startup.ServiceProvider, builder.Configuration);
}
app.MapScenarioReset();
app.MapDockImages();
app.MapSimulation();

app.Run();
