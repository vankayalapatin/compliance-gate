// Skia smoke test: the vision build pipeline decodes base images through
// SkiaSharp, so a broken native runtime must fail here with a name, not as
// a host crash inside the composite step.
namespace Gate.Eval;

public sealed class VisionSkiaProbeTests
{
    [Fact]
    public void SkiaDecodeOnly()
    {
        var file = Path.Combine(VisionFiles.RepoRoot(), "assets", "dock", "IMG-01-base.jpg");
        using var bitmap = SkiaSharp.SKBitmap.Decode(file);
        Assert.NotNull(bitmap);
        Assert.True(bitmap!.Width > 0);
    }
}
