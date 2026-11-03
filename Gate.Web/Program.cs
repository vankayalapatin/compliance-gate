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
// Published rule packs live in the app data dir (git-ignored live state),
// never in the source tree: an approved memo lands as a new versioned
// file here, and the engine enforces it on the next evaluation with no
// rebuild and no restart. The directory starts empty (embedded bases win
// until the first publication) and is created at startup.
var packsDir = Path.GetFullPath(
    builder.Configuration[ComplianceEngine.PackDirectoryConfigKey]
        ?? ComplianceEngine.DefaultPackDirectory,
    builder.Environment.ContentRootPath);
Directory.CreateDirectory(packsDir);
builder.Services.AddSingleton(sp => new ComplianceEngine(
    ComplianceEngine.OptionsFromConfig(key => builder.Configuration[key])));
builder.Services.AddSingleton<IComplianceEngine>(sp => sp.GetRequiredService<ComplianceEngine>());
builder.Services.AddSingleton<IErpPolicy>(sp => new ErpPolicy(sp.GetRequiredService<ComplianceEngine>()));
builder.Services.AddSingleton<IErpPolicyContext>(sp => new CasePolicyContext(
    CaseCacheStore.ResolveDir(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath)));
builder.Services.AddScoped<IErpAdapter>(sp =>
    new EfCoreErpAdapter(
        sp.GetRequiredService<IDbContextFactory<ErpDbContext>>(),
        sp.GetRequiredService<IErpPolicy>(),
        sp.GetRequiredService<IErpPolicyContext>()));

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
builder.Services.AddSingleton<SplitProposals>();
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
    // The library path labels every shipment from the scene catalog; the
    // legacy spec file stays for the older tests only.
    return new DockImageOptions(
        Path.Combine(assetsDir, "dock-scenes", "bases"),
        Path.Combine(assetsDir, "dock-scenes", "scenes.json"),
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
    dockImageOptions: sp.GetRequiredService<DockImageOptions>(),
    countryNames: sp.GetRequiredService<CountryNames>().Map,
    splitProposals: sp.GetRequiredService<SplitProposals>(),
    turnMaxTokens: sp.GetRequiredService<TurnBudgetOptions>().TurnMaxTokens,
    dockSceneLive: sp.GetRequiredService<DockSceneLive>()));
// Turn token budget from config (MetaApi:TurnMaxTokens).
builder.Services.AddSingleton(TurnBudgetOptions.FromConfig(builder.Configuration));
// Dock scene source: live generated scenes whenever the image settings
// resolve (MetaApi address and key plus the scene model), so shipments
// build live by default; the Admin switch can still turn live off. Without
// the settings the generator stays null, one startup line names what's
// missing, and the library stays in use.
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var options = DockSceneStatus.ImageOptionsOrNull(config);
    ISceneGenerator? generator = options is null
        ? null
        : new MetaSceneGenerator(new HttpClient(), options);
    return new DockSceneLive { SceneGenerator = generator, LiveOn = generator is not null };
});
builder.Services.AddSingleton<DockSceneStatus>();
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
    var scenes = services.GetRequiredService<DockSceneLive>();
    if (scenes.SceneGenerator is null)
    {
        app.Logger.LogWarning(
            "Live dock scenes off ({Missing} not set); shipment photos use the photo library.",
            string.Join(", ", DockSceneStatus.MissingSettings(builder.Configuration)));
    }
}
app.MapAgentPickup();
app.MapDockImages();
app.MapSimulation();

app.Run();
