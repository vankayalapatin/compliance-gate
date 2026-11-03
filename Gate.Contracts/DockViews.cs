// Dock views minted for one case: the image file names the dock pipeline
// built (initial, rebuild, reshot) for the case's ASN this lifetime. The
// dock counter offers only recorded files on disk, so views minted in a
// prior lifetime are never offered after a reset; the reset deletes the
// recorded files plus the image cache. File names only, never paths.
namespace Gate.Contracts;

public sealed record DockViewFiles(
    string CaseId,
    string AsnNumber,
    IReadOnlyList<string> Images,
    DateTimeOffset UpdatedAt);
