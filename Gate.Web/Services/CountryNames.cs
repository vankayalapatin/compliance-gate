// Country display names from company-owned config: ISO code to the name
// every display shows, so China reads China everywhere instead of whatever
// the machine's region settings say. Unknown codes render as themselves;
// nothing is guessed. Tests build their own maps.
using Microsoft.Extensions.Configuration;

namespace Gate.Web.Services;

public sealed class CountryNames
{
    private readonly IReadOnlyDictionary<string, string> _map;

    public CountryNames(IReadOnlyDictionary<string, string>? map = null) =>
        _map = new Dictionary<string, string>(map ?? new Dictionary<string, string>(), StringComparer.Ordinal);

    public static CountryNames FromConfig(IConfiguration? config)
    {
        var map = config?.GetSection("CountryNames").Get<Dictionary<string, string>>();
        return new CountryNames(map);
    }

    public IReadOnlyDictionary<string, string> Map => _map;

    public string Display(string code) =>
        _map.TryGetValue(code, out var name) ? name : code;
}
