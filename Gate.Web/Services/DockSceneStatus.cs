// Dock scene status for the Admin live build: whether the image settings
// are present, which scene model applies, and the one-line status the
// switch renders. The switch itself lives on the shared DockSceneLive;
// this helper only reads config and formats status. Address and key come
// from the MetaApi settings; the model is the MetaImages scene model,
// defaulting to the image model.
using Gate.Agent;
using Microsoft.Extensions.Configuration;

namespace Gate.Web.Services;

public sealed class DockSceneStatus
{
    private readonly IConfiguration _config;

    public DockSceneStatus(IConfiguration config) => _config = config;

    public static string? SceneModelName(IConfiguration config)
    {
        var section = config.GetSection("MetaImages");
        var model = section["SceneModel"];
        return string.IsNullOrWhiteSpace(model) ? section["ImageModel"] : model;
    }

    /// <summary>Image-generation options from config, or null when any
    /// setting is missing. The app wires the generator and turns live on
    /// exactly when this resolves.</summary>
    public static MetaSceneOptions? ImageOptionsOrNull(IConfiguration config)
    {
        var api = config.GetSection("MetaApi");
        var baseUrl = api["BaseUrl"];
        var key = api["ApiKey"];
        var model = SceneModelName(config);
        return string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(key)
            || string.IsNullOrWhiteSpace(model)
            ? null
            : new MetaSceneOptions(baseUrl, model, key);
    }

    /// <summary>Names of the missing image settings, for the one startup
    /// line. Empty when the generator can be wired.</summary>
    public static IReadOnlyList<string> MissingSettings(IConfiguration config)
    {
        var api = config.GetSection("MetaApi");
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(api["BaseUrl"]))
        {
            missing.Add("MetaApi:BaseUrl");
        }

        if (string.IsNullOrWhiteSpace(api["ApiKey"]))
        {
            missing.Add("MetaApi:ApiKey");
        }

        if (string.IsNullOrWhiteSpace(SceneModelName(config)))
        {
            missing.Add("MetaImages:SceneModel");
        }

        return missing;
    }

    public bool HasCredentials => ImageOptionsOrNull(_config) is not null;

    public string? MissingCredentialsNote =>
        HasCredentials
            ? null
            : "Live dock scenes are off: the image service needs its address, model, and key " +
              "before this switch can build live scenes.";

    public string StatusLine(bool liveOn, bool generatorWired, string? fallbackReason)
    {
        var source = liveOn && generatorWired ? "live build on" : "photo library";
        return string.IsNullOrWhiteSpace(fallbackReason)
            ? $"Dock scenes: {source}."
            : $"Dock scenes: {source}; last run fell back to the library ({fallbackReason.Trim()}).";
    }
}
