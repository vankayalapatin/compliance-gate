// Programmatic label compositing: the deterministic fallback behind AI
// labeling, and the mask builder that constrains AI edits to label regions.
// All label contents arrive resolved (ASN + PO + truth); nothing is invented.
using SkiaSharp;

namespace Gate.Agent;

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
