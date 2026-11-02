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
// Real ERP: EF Core over sqlite, one context per adapter call from the
// factory. A single scoped context breaks under interleaved Blazor circuit
// handlers ("a second operation was started on this context") and serves
// stale tracked reads; per-call contexts keep handlers independent and
// reads fresh. The in-memory fake stays for unit tests only and never
// backs the app.
var erpSqlite = Path.GetFullPath(BaselineRestore.LiveErpPath(builder.Configuration));
var erpDir = Path.GetDirectoryName(erpSqlite);
if (!string.IsNullOrEmpty(erpDir))
{
    Directory.CreateDirectory(erpDir);
}
builder.Services.AddDbContextFactory<ErpDbContext>(options => options.UseSqlite($"Data Source={erpSqlite}"));
// Factory lambda, not the bare type: the adapter also carries a unit-test
// options constructor, and DI refuses the ambiguous pair. Production
// always resolves through the context factory.
builder.Services.AddSingleton<IErpPolicy, ErpPolicy>();
builder.Services.AddSingleton<IErpPolicyContext>(sp => new CasePolicyContext(
    CaseCacheStore.ResolveDir(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath)));
builder.Services.AddScoped<IErpAdapter>(sp =>
    new EfCoreErpAdapter(
        sp.GetRequiredService<IDbContextFactory<ErpDbContext>>(),
        sp.GetRequiredService<IErpPolicy>(),
        sp.GetRequiredService<IErpPolicyContext>()));
builder.Services.AddSingleton<IComplianceEngine, ComplianceEngine>();
// Spend ledger: process-wide, config-driven (ModelSpend section). The
// meter wraps the live client only, between the cache and Spark, so
// cached answers and fake echo calls never accrue spend.
builder.Services.AddSingleton(sp => new ModelSpendTracker(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IModelClient>(sp =>
{
    if (string.IsNullOrWhiteSpace(spark.ApiKey))
    {
        return new CachedModelClient(new FakeModelClient(), ModelCachePaths.ResolveDir());
    }

    // The transport timeout must clear the longest attempt timeout (the
    // memo budget from config) with headroom, or HttpClient's 100-second
    // default silently becomes every call's real timeout. Attempt timeouts
    // still fire first; this is only the backstop for direct calls.
    var sparkHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(spark.MemoTimeoutSeconds + 60),
    };
    var metered = new MeteredModelClient(
        new SparkModelClient(sparkHttp, spark),
        sp.GetRequiredService<ModelSpendTracker>(),
        sp.GetRequiredService<ILogger<MeteredModelClient>>());
    return new CachedModelClient(metered, ModelCachePaths.ResolveDir());
});
builder.Services.AddScoped<P2pAgent>();
builder.Services.AddSingleton<IDockEvaluator, DockEvaluatorService>();
builder.Services.AddSingleton<PresentationPace>();
builder.Services.AddSingleton(sp => CountryNames.FromConfig(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IImageLabeler>(sp =>
{
    // Image-edit credentials come from config or the environment
    // (MetaImages:BaseUrl/ImageModel/ApiKey), like the Spark key. Without
    // them the pipeline composites labels and reads them live.
    var section = sp.GetRequiredService<IConfiguration>().GetSection("MetaImages");
    var baseUrl = section["BaseUrl"];
    var model = section["ImageModel"];
    var key = section["ApiKey"];
    return string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(key)
        ? new NullImageLabeler()
        : new MetaImageLabeler(new HttpClient(), new MetaImageOptions(baseUrl, model, key));
});
builder.Services.AddSingleton(sp =>
{
    var dockDir = DockPhotoService.ResolveImageDir();
    var assetsDir = Directory.GetParent(dockDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName ?? dockDir;
    var cacheDir = CaseCacheStore.ResolveDir(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath);
    return new DockImageOptions(
        dockDir,
        Path.Combine(assetsDir, "image-specs.json"),
        Path.Combine(assetsDir, "cartons.json"),
        dockDir,
        Path.Combine(dockDir, ".image-cache"),
        Path.Combine(cacheDir, "vision-reads"));
});
builder.Services.AddScoped(sp => new CaseRunner(
    sp.GetRequiredService<IErpAdapter>(),
    sp.GetRequiredService<P2pAgent>(),
    sp.GetRequiredService<IModelClient>(),
    sp.GetRequiredService<IComplianceEngine>(),
    sp.GetRequiredService<IDockEvaluator>(),
    CaseCacheStore.ResolveDir(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath),
    sp.GetRequiredService<ILogger<CaseRunner>>(),
    pace: sp.GetRequiredService<PresentationPace>(),
    imageLabeler: sp.GetRequiredService<IImageLabeler>(),
    dockImageOptions: sp.GetRequiredService<DockImageOptions>()));
builder.Services.AddSingleton(sp => new CaseCacheStore(
    CaseCacheStore.ResolveDir(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath)));
builder.Services.AddSingleton(sp => new MemoFileStore(
    CaseCacheStore.ResolveDir(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath)));
builder.Services.AddScoped(sp => new MemoRunner(
    sp.GetRequiredService<IErpAdapter>(),
    sp.GetRequiredService<IModelClient>(),
    sp.GetRequiredService<IComplianceEngine>(),
    CaseCacheStore.ResolveDir(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath),
    memoTimeout: TimeSpan.FromSeconds(sp.GetRequiredService<SparkOptions>().MemoTimeoutSeconds)));
builder.Services.AddSingleton(sp => new DockPhotoService(
    DockPhotoService.ResolveImageDir(), DockPhotoService.ResolveVisionCacheDir()));
builder.Services.AddSingleton<ReplayState>();
builder.Services.AddScoped<AgentActivityService>();
builder.Services.AddScoped<CaseViewService>();
builder.Services.AddScoped<ErpViewService>();
builder.Services.AddScoped<ErpActionService>();
builder.Services.AddScoped<ActingUserState>();
builder.Services.AddScoped<DecisionOutcomeService>();
builder.Services.AddScoped<PolicyViewService>();
builder.Services.AddScoped<PolicyOutcomeService>();
builder.Services.AddScoped<GovernanceViewService>();
builder.Services.AddScoped<KbViewService>();
builder.Services.AddScoped<KbEditService>();
builder.Services.AddScoped(sp => new MemoSubmitService(
    sp.GetRequiredService<MemoRunner>(),
    sp.GetRequiredService<MemoFileStore>(),
    MemoSubmitService.ResolveDir(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath),
    sp.GetRequiredService<ILogger<MemoSubmitService>>()));
builder.Services.AddScoped(sp => new AskWhyService(
    sp.GetRequiredService<P2pAgent>(),
    sp.GetRequiredService<IModelClient>(),
    sp.GetRequiredService<IErpAdapter>(),
    sp.GetRequiredService<CaseCacheStore>(),
    sp.GetRequiredService<DockPhotoService>(),
    sp.GetRequiredService<ReplayState>(),
    sp.GetRequiredService<ILogger<AskWhyService>>(),
    ModelCachePaths.ResolveDir()));
builder.Services.AddScoped(sp => new ReplayService(
    sp.GetRequiredService<CaseCacheStore>(),
    sp.GetRequiredService<DockPhotoService>(),
    sp.GetRequiredService<ReplayState>(),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<ReplayService>>(),
    sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath,
    sp.GetRequiredService<ModelSpendTracker>()));
// Outside-world surface: posts the human's external inputs back to this
// app's own simulation endpoints. The generous timeout covers photo
// transcription (live model calls can take minutes). The service retries
// transport failures once with the same idempotency key, so a lost
// response never double-injects the input.
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

// Startup restore: with missing live stores the shipped snapshot pair is
// copied into the live paths (logged as 'Baseline restored'); otherwise
// the app starts on its live stores. Then migrations bring the ERP
// database current. Zero model calls on this path.
using (var startup = app.Services.CreateScope())
{
    var services = startup.ServiceProvider;
    await BaselineRestore.EnsureAsync(
        app.Environment.ContentRootPath,
        builder.Configuration,
        services.GetRequiredService<ILoggerFactory>().CreateLogger("BaselineRestore"));
    var factory = services.GetRequiredService<IDbContextFactory<ErpDbContext>>();
    using var db = factory.CreateDbContext();
    await db.Database.MigrateAsync();
}
app.MapAgentPickup();
app.MapDockImages();
app.MapSimulation();

app.Run();
