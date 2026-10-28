// Live proof: one real Spark call, then the identical call served from cache
// with zero additional HTTP. The key comes from user secrets (MetaApi:ApiKey)
// or the environment — never from a file in the repo.
using System.Net;
using Gate.Contracts;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class SparkLiveTests
{
    [Fact]
    public async Task RealSparkCallWorksAndGetsCached()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["MetaApi:ApiKey"];
        var model = config["MetaApi:Model"];
        var baseUrl = config["MetaApi:BaseUrl"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey),
            "MetaApi:ApiKey is missing. Set it with: dotnet user-secrets set \"MetaApi:ApiKey\" \"<key>\" --project Gate.Web (plus MetaApi:Model).");
        Assert.False(string.IsNullOrWhiteSpace(model), "MetaApi:Model is missing. Set it with: dotnet user-secrets set \"MetaApi:Model\" \"<model>\" --project Gate.Web.");

        var counter = new CountingHandler(new HttpClientHandler());
        using var http = new HttpClient(counter);
        var spark = new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.llama.com/v1" : baseUrl,
            Model = model!,
            ApiKey = apiKey!
        });
        var cacheDir = Path.Combine(Path.GetTempPath(), "gate-spark-live");
        var cached = new CachedModelClient(spark, cacheDir);
        var request = new ModelRequest("Reply with exactly: spark-ok", "You are a connectivity probe.");

        var first = await cached.CompleteAsync(request);
        Assert.Contains("spark-ok", first.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, counter.Count);
        Assert.True(File.Exists(Path.Combine(cacheDir, CachedModelClient.CacheKey(model!, request) + ".json")));

        var second = await cached.CompleteAsync(request);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(1, counter.Count);
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            return base.SendAsync(request, ct);
        }
    }
}
