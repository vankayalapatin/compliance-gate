// Programmatic label compositing: the deterministic fallback behind AI
// labeling, and the mask builder that constrains AI edits to label regions.
// All label contents arrive resolved (ASN + PO + truth); nothing is invented.
// The library path adds photographic problem cues (damage, occlusion) drawn
// from the shipment's problem list; those cues are deterministic per carton.
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace Gate.Agent;

/// <summary>Fractional face rectangle (origin top-left) for occlusion.</summary>
public sealed record FaceRect(double X, double Y, double W, double H);

/// <summary>One crushed carton: its label rect plus the carton id that
/// seeds the deterministic crease pattern.</summary>
public sealed record DamageCue(string CartonId, double X, double Y, double W, double H);

internal static class DockLabelCompositor
{
    public static byte[] Composite(byte[] workBase, IReadOnlyList<DockImagePipeline.ResolvedLabel> labels, ViewSpec view)
    {
        using var source = SKBitmap.Decode(workBase);
        if (source is null)
        {
            throw new InvalidOperationException($"View {view.Id}: base bytes do not decode.");
        }

        using var work = new SKBitmap(source.Width, source.Height);
        using (var draw = new SKCanvas(work))
        {
            draw.DrawBitmap(source, 0, 0);
            foreach (var label in labels)
            {
                DrawLabel(draw, work.Width, work.Height, label);
            }
        }

        using var image = SKImage.FromBitmap(work);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    /// <summary>Library composite: occlusions first (hidden faces stay
    /// label-free), then damage cues around crushed labels, then the
    /// labels on top so text stays legible. Deterministic per input.</summary>
    public static byte[] CompositeLibrary(
        byte[] workBase,
        IReadOnlyList<DockImagePipeline.ResolvedLabel> labels,
        IReadOnlyList<DamageCue> damage,
        IReadOnlyList<FaceRect> occlude,
        string viewId)
    {
        using var source = SKBitmap.Decode(workBase);
        if (source is null)
        {
            throw new InvalidOperationException($"View {viewId}: base bytes do not decode.");
        }

        using var work = new SKBitmap(source.Width, source.Height);
        using (var draw = new SKCanvas(work))
        {
            draw.DrawBitmap(source, 0, 0);
            foreach (var hidden in occlude)
            {
                DrawOcclusion(draw, work.Width, work.Height, hidden);
            }

            foreach (var cue in damage)
            {
                DrawDamageCue(draw, work.Width, work.Height, cue);
            }

            foreach (var label in labels)
            {
                DrawLabel(draw, work.Width, work.Height, label);
            }
        }

        using var image = SKImage.FromBitmap(work);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    public static byte[] Crop(byte[] baseJpeg, CropSpec crop)
    {
        using var source = SKBitmap.Decode(baseJpeg);
        if (source is null)
        {
            throw new InvalidOperationException("Crop base bytes do not decode.");
        }

        var rect = new SKRectI(
            (int)(crop.X * source.Width), (int)(crop.Y * source.Height),
            (int)((crop.X + crop.W) * source.Width), (int)((crop.Y + crop.H) * source.Height));
        using var cropped = new SKBitmap(rect.Width * crop.Scale, rect.Height * crop.Scale);
        using (var draw = new SKCanvas(cropped))
        {
            draw.DrawBitmap(source, rect, new SKRect(0, 0, cropped.Width, cropped.Height));
        }

        using var image = SKImage.FromBitmap(cropped);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] BuildMask(byte[] workBase, IReadOnlyList<DockImagePipeline.ResolvedLabel> labels)
    {
        using var source = SKBitmap.Decode(workBase);
        if (source is null)
        {
            throw new InvalidOperationException("Mask base bytes do not decode.");
        }

        using var mask = new SKBitmap(source.Width, source.Height);
        using (var draw = new SKCanvas(mask))
        {
            draw.Clear(SKColors.Black);
            using var white = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            foreach (var label in labels)
            {
                draw.DrawRect(
                    new SKRect(
                        (float)(label.X * source.Width), (float)(label.Y * source.Height),
                        (float)((label.X + label.W) * source.Width), (float)((label.Y + label.H) * source.Height)),
                    white);
            }
        }

        using var image = SKImage.FromBitmap(mask);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static bool Decodes(byte[] bytes)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            return bitmap is not null && bitmap.Width > 0 && bitmap.Height > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void DrawLabel(
        SKCanvas draw, int imageW, int imageH, DockImagePipeline.ResolvedLabel label)
    {
        var x = (float)(label.X * imageW);
        var y = (float)(label.Y * imageH);
        var w = (float)(label.W * imageW);
        var h = (float)(label.H * imageH);

        using var white = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
        using var black = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, w / 120) };
        using var blackFill = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var red = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, w / 150), IsAntialias = true };
        using var yellow = new SKPaint { Color = SKColors.Yellow, Style = SKPaintStyle.Fill };
        var box = new SKRect(x, y, x + w, y + h);
        draw.DrawRect(box, white);
        draw.DrawRect(box, black);

        var pad = w * 0.04f;
        var markW = Math.Min(w * 0.3f, h * 0.55f);
        var textWidth = w - pad * 2 - markW;
        using var text = new SKPaint
        {
            Color = SKColors.Black, IsAntialias = true,
            Typeface = SKTypeface.Default, TextSize = h * 0.125f
        };
        using var bold = new SKPaint
        {
            Color = SKColors.Black, IsAntialias = true, FakeBoldText = true,
            Typeface = SKTypeface.Default, TextSize = h * 0.19f
        };
        var lineX = x + pad;
        var lineY = y + pad + h * 0.15f;
        var lineStep = h * 0.135f;
        var lines = new[]
        {
            $"PART {label.Part}",
            $"PO {label.PoNumber}",
            $"LOT {label.Lot}",
            $"QTY {label.Units}",
            "UN3480"
        };
        FitText(bold, [label.CartonId], textWidth);
        FitText(text, lines, textWidth);
        draw.DrawText(label.CartonId, lineX, lineY, bold);
        foreach (var line in lines)
        {
            lineY += lineStep;
            draw.DrawText(line, lineX, lineY, text);
        }

        // Lithium-battery mark: red-bordered square with hatch plus UN number,
        // omitted only on the seeded missing-mark face. The Class 9 diamond
        // below it prints on every BAT-1 label either way.
        var mark = new SKRect(x + w - pad - markW, y + pad, x + w - pad, y + pad + markW);
        using var markText = new SKPaint
        {
            Color = SKColors.Black, IsAntialias = true,
            Typeface = SKTypeface.Default, TextSize = markW * 0.22f
        };
        if (label.MarkPresent)
        {
            draw.DrawRect(mark, white);
            draw.DrawRect(mark, red);
            draw.Save();
            draw.ClipRect(mark);
            for (var d = -mark.Height; d < mark.Width + mark.Height; d += Math.Max(4, markW / 12))
            {
                draw.DrawLine(mark.Left + d, mark.Bottom, mark.Left + d + mark.Height, mark.Top, red);
            }

            draw.Restore();
            draw.DrawText("UN3480", mark.Left + markW * 0.06f, mark.Top + markW * 0.58f, markText);
        }

        // Class 9 diamond below the mark zone.
        var cx = mark.Left + markW / 2;
        var cy = mark.Bottom + (box.Bottom - pad - mark.Bottom) / 2;
        var r = Math.Min(markW / 2, (box.Bottom - pad - mark.Bottom) / 2) * 0.9f;
        if (r > 4)
        {
            using var diamond = new SKPath();
            diamond.MoveTo(cx, cy - r);
            diamond.LineTo(cx + r, cy);
            diamond.LineTo(cx, cy + r);
            diamond.LineTo(cx - r, cy);
            diamond.Close();
            draw.DrawPath(diamond, yellow);
            draw.DrawPath(diamond, blackFill);
            draw.DrawText("9", cx - r * 0.18f, cy + r * 0.32f, markText);
        }
    }

    /// <summary>Crush cue around a label: dent shading, three jagged
    /// creases radiating outward, and a torn corner. Drawn under the
    /// label (the label covers the center), so the cue rings the text.
    /// Crease angles vary deterministically per carton id.</summary>
    private static void DrawDamageCue(SKCanvas draw, int imageW, int imageH, DamageCue cue)
    {
        var x = (float)(cue.X * imageW);
        var y = (float)(cue.Y * imageH);
        var w = (float)(cue.W * imageW);
        var h = (float)(cue.H * imageH);
        var zone = new SKRect(
            Math.Max(0, x - w * 0.35f), Math.Max(0, y - h * 0.35f),
            Math.Min(imageW, x + w * 1.35f), Math.Min(imageH, y + h * 1.35f));

        using var dent = new SKPaint { Color = new SKColor(40, 25, 15, 90), Style = SKPaintStyle.Fill };
        draw.DrawRect(zone, dent);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(cue.CartonId));
        using var crease = new SKPaint
        {
            Color = new SKColor(35, 22, 14), Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2, w / 90), IsAntialias = true
        };
        var corners = new[]
        {
            (zone.Left, zone.Top), (zone.Right, zone.Top),
            (zone.Left, zone.Bottom), (zone.Right, zone.Bottom)
        };
        for (var i = 0; i < 3; i++)
        {
            var (sx, sy) = corners[hash[i] % corners.Length];
            var ex = x + w * ((hash[i + 3] % 100) / 100f);
            var ey = y + h * ((hash[i + 6] % 100) / 100f);
            using var path = new SKPath();
            path.MoveTo(sx, sy);
            var steps = 4;
            for (var s = 1; s <= steps; s++)
            {
                var t = s / (float)steps;
                var wobble = (((hash[(i + s) % hash.Length] % 21) - 10) / 10f) * w * 0.06f;
                path.LineTo(sx + (ex - sx) * t + wobble * (s % 2 == 0 ? 1 : -1), sy + (ey - sy) * t);
            }

            draw.DrawPath(path, crease);
        }

        // Torn corner: dark triangle with a lighter torn-paper inner edge.
        var tx = zone.Right - w * 0.28f;
        var ty = zone.Bottom - h * 0.32f;
        using var tear = new SKPath();
        tear.MoveTo(zone.Right, ty);
        tear.LineTo(tx, zone.Bottom);
        tear.LineTo(zone.Right, zone.Bottom);
        tear.Close();
        using var tearFill = new SKPaint { Color = new SKColor(48, 30, 18), Style = SKPaintStyle.Fill };
        using var tearEdge = new SKPaint
        {
            Color = SKColors.Black, Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2, w / 110), IsAntialias = true
        };
        draw.DrawPath(tear, tearFill);
        draw.DrawPath(tear, tearEdge);
        using var tearInner = new SKPaint
        {
            Color = new SKColor(196, 164, 128), Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1, w / 220), IsAntialias = true
        };
        draw.DrawLine(zone.Right, ty + h * 0.05f, tx + w * 0.05f, zone.Bottom, tearInner);
    }

    /// <summary>Hidden-face cover: a dark strap-crossed panel over the face
    /// so the carton reads as covered rather than merely unlabeled.</summary>
    private static void DrawOcclusion(SKCanvas draw, int imageW, int imageH, FaceRect face)
    {
        var x = (float)(face.X * imageW);
        var y = (float)(face.Y * imageH);
        var w = (float)(face.W * imageW);
        var h = (float)(face.H * imageH);
        var zone = new SKRect(
            Math.Max(0, x - w * 0.15f), Math.Max(0, y - h * 0.15f),
            Math.Min(imageW, x + w * 1.15f), Math.Min(imageH, y + h * 1.15f));

        using var cover = new SKPaint { Color = new SKColor(52, 46, 40), Style = SKPaintStyle.Fill };
        draw.DrawRect(zone, cover);
        using var strap = new SKPaint
        {
            Color = new SKColor(28, 24, 21), Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(3, w / 60), IsAntialias = true
        };
        draw.DrawLine(zone.Left, zone.Top, zone.Right, zone.Bottom, strap);
        draw.DrawLine(zone.Left, zone.Bottom, zone.Right, zone.Top, strap);
        using var shade = new SKPaint
        {
            Color = new SKColor(15, 12, 10), Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2, w / 100), IsAntialias = true
        };
        draw.DrawRect(zone, shade);
    }

    private static void FitText(SKPaint paint, string[] lines, float maxWidth)
    {
        foreach (var line in lines)
        {
            var wide = paint.MeasureText(line);
            if (wide > maxWidth && wide > 0)
            {
                paint.TextSize = Math.Max(6, paint.TextSize * maxWidth / wide);
            }
        }
    }
}
