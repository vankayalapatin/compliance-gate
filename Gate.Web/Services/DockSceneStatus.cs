// Dock scene status for the Admin live build: whether MetaImages
// credentials are present, which scene model applies, and the one-line
// status the switch renders. The switch itself lives on the shared
// DockSceneLive; this helper only reads config and formats status.
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

    public bool HasCredentials
    {
        get
        {
            var section = _config.GetSection("MetaImages");
            return !string.IsNullOrWhiteSpace(section["BaseUrl"])
                && !string.IsNullOrWhiteSpace(SceneModelName(_config))
                && !string.IsNullOrWhiteSpace(section["ApiKey"]);
        }
    }

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
