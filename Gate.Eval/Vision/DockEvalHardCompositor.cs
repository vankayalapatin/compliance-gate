// Vision-owned compositor for the hard tier: several small rotated labels
// per pallet photo, then one photographic effect (none, blur, glare, cool)
// over the whole composite so labels degrade like the scene. Deterministic
// per image and carton id. Damage stays photographic (dent bases only).
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

namespace Gate.Eval;

internal static class DockEvalHardCompositor
{
    public static (byte[] Jpeg, string Effect) BuildFinal(
        byte[] baseJpeg, JsonElement photo, string imageId)
    {
        using var source = SKBitmap.Decode(baseJpeg);
        if (source is null)
        {
            throw new InvalidOperationException($"EvalHard {imageId}: base bytes do not decode.");
        }

        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(imageId));
        var brightness = (hash[0] % 31) - 15; // -15..+15
        var effect = (hash[1] % 4) switch { 1 => "blur", 2 => "glare", 3 => "cool", _ => "none" };

        using var work = new SKBitmap(source.Width, source.Height);
        using (var draw = new SKCanvas(work))
        {
            draw.DrawBitmap(source, 0, 0);
            if (brightness > 0)
            {
                using var white = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(brightness * 2)) };
                draw.DrawRect(0, 0, work.Width, work.Height, white);
            }
            else if (brightness < 0)
            {
                using var black = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(-brightness * 2)) };
                draw.DrawRect(0, 0, work.Width, work.Height, black);
            }

            foreach (var carton in photo.GetProperty("cartons").EnumerateArray())
            {
                DrawLabel(draw, work.Width, work.Height, carton.GetProperty("label"));
            }
        }

        using var out_ = new SKBitmap(work.Width, work.Height);
        using (var draw = new SKCanvas(out_))
        {
            if (string.Equals(effect, "blur", StringComparison.Ordinal))
            {
                using var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(1.0f, 1.0f) };
                draw.DrawBitmap(work, 0, 0, blur);
            }
            else
            {
                draw.DrawBitmap(work, 0, 0);
                if (string.Equals(effect, "glare", StringComparison.Ordinal))
                {
                    using var glare = new SKPaint
                    {
                        Shader = SKShader.CreateLinearGradient(
                            new SKPoint(0, 0), new SKPoint(work.Width, work.Height),
                            [new SKColor(255, 255, 255, 55), new SKColor(255, 255, 255, 0)],
                            [0.0f, 0.7f], SKShaderTileMode.Clamp)
                    };
                    draw.DrawRect(0, 0, work.Width, work.Height, glare);
                }
                else if (string.Equals(effect, "cool", StringComparison.Ordinal))
                {
                    using var cool = new SKPaint { Color = new SKColor(140, 170, 255, 22) };
                    draw.DrawRect(0, 0, work.Width, work.Height, cool);
                }
            }
        }

        using var image = SKImage.FromBitmap(out_);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return (data.ToArray(), effect);
    }

    private static void DrawLabel(SKCanvas draw, int imageW, int imageH, JsonElement label)
    {
        var cx = (float)(label.GetProperty("cx").GetDouble() * imageW);
        var cy = (float)(label.GetProperty("cy").GetDouble() * imageH);
        var w = (float)(label.GetProperty("w").GetDouble() * imageW);
        var h = (float)(label.GetProperty("h").GetDouble() * imageH);
        var idHash = SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(label.GetProperty("carton_id").GetString()!));
        var angle = (idHash[0] % 21) - 10; // -10..+10 degrees

        draw.Save();
        draw.RotateDegrees(angle, cx, cy);
        try
        {
            var x = cx - w / 2;
            var y = cy - h / 2;
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
            var cartonId = label.GetProperty("carton_id").GetString()!;
            var lines = new[]
            {
                $"PART {label.GetProperty("part").GetString()}",
                $"PO {label.GetProperty("po_number").GetString()}",
                $"LOT {label.GetProperty("lot_code").GetString()}",
                $"QTY {label.GetProperty("units").GetInt32()}",
                label.GetProperty("un_number").GetString()!
            };
            FitText(bold, [cartonId], textWidth);
            FitText(text, lines, textWidth);
            var lineX = x + pad;
            var lineY = y + pad + h * 0.15f;
            var lineStep = h * 0.135f;
            draw.DrawText(cartonId, lineX, lineY, bold);
            float lotLineY = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                lineY += lineStep;
                draw.DrawText(lines[i], lineX, lineY, text);
                if (i == 2)
                {
                    lotLineY = lineY;
                }
            }

            if (label.TryGetProperty("smudge_lot", out var smudge) && smudge.ValueKind == JsonValueKind.True)
            {
                var lotWidth = text.MeasureText(lines[2]);
                var barH = text.TextSize * 1.1f;
                using var gray = new SKPaint { Color = new SKColor(90, 90, 90), Style = SKPaintStyle.Fill };
                using var dark = new SKPaint { Color = new SKColor(40, 40, 40), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, barH / 8) };
                var bar = new SKRect(lineX, lotLineY - barH * 0.8f, lineX + lotWidth, lotLineY + barH * 0.25f);
                draw.DrawRect(bar, gray);
                for (var d = 0f; d < bar.Width + bar.Height; d += Math.Max(4f, bar.Height / 3f))
                {
                    draw.DrawLine(bar.Left + d, bar.Bottom, bar.Left + d + bar.Height, bar.Top, dark);
                }
            }

            var mark = new SKRect(x + w - pad - markW, y + pad, x + w - pad, y + pad + markW);
            using var markText = new SKPaint
            {
                Color = SKColors.Black, IsAntialias = true,
                Typeface = SKTypeface.Default, TextSize = markW * 0.22f
            };
            if (label.GetProperty("mark_present").GetBoolean())
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
                draw.DrawText(label.GetProperty("un_number").GetString()!, mark.Left + markW * 0.06f, mark.Top + markW * 0.58f, markText);
            }

            if (!label.TryGetProperty("class9", out var class9) || class9.ValueKind == JsonValueKind.True)
            {
                var mcx = mark.Left + markW / 2;
                var mcy = mark.Bottom + (box.Bottom - pad - mark.Bottom) / 2;
                var r = Math.Min(markW / 2, (box.Bottom - pad - mark.Bottom) / 2) * 0.9f;
                if (r > 4)
                {
                    using var diamond = new SKPath();
                    diamond.MoveTo(mcx, mcy - r);
                    diamond.LineTo(mcx + r, mcy);
                    diamond.LineTo(mcx, mcy + r);
                    diamond.LineTo(mcx - r, mcy);
                    diamond.Close();
                    draw.DrawPath(diamond, yellow);
                    draw.DrawPath(diamond, blackFill);
                    draw.DrawText("9", mcx - r * 0.18f, mcy + r * 0.32f, markText);
                }
            }
        }
        finally
        {
            draw.Restore();
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
