// Turn token budget from config (MetaApi:TurnMaxTokens). Web-side holder:
// Program registers it from configuration and hands it to the case
// runner; a missing value leaves the runner default in force.
using Microsoft.Extensions.Configuration;

namespace Gate.Web.Services;

public sealed record TurnBudgetOptions(int? TurnMaxTokens)
{
    public static TurnBudgetOptions FromConfig(IConfiguration config) =>
        new(config.GetValue<int?>("MetaApi:TurnMaxTokens"));
}
