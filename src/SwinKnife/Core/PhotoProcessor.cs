using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwinKnife.Core;

/// <summary>Immagine BGRA a 8 bit (alfa non premoltiplicato).</summary>
public sealed class Bgra(int width, int height, byte[] pixels)
{
    public int W { get; } = width;
    public int H { get; } = height;
    public byte[] Px { get; } = pixels;

    public Bgra(int width, int height) : this(width, height, new byte[width * height * 4]) { }

    public BitmapSource ToBitmap()
    {
        var b = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, Px, W * 4);
        b.Freeze();
        return b;
    }

    public static Bgra FromBitmap(BitmapSource src)
    {
        var b = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var img = new Bgra(b.PixelWidth, b.PixelHeight);
        b.CopyPixels(img.Px, img.W * 4, 0);
        return img;
    }
}

/// <summary>Stato delle modifiche (come "Foto" di iPhone): sempre ricalcolabile dall'originale.</summary>
public sealed class EditState
{
    public Dictionary<string, double> Adj { get; init; } = new();
    public string Filter { get; set; } = "none";
    public double FilterAmount { get; set; } = 1;
    public int Rot90 { get; set; }           // quarti di giro antiorari
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }
    public double Straighten { get; set; }   // gradi, positivo = orario
    public Rect Crop { get; set; } = new(0, 0, 1, 1); // normalizzato sull'immagine raddrizzata

    public double A(string key) => Adj.TryGetValue(key, out var v) ? v : 0;

    public EditState Clone() => new()
    {
        Adj = new Dictionary<string, double>(Adj), Filter = Filter, FilterAmount = FilterAmount, Rot90 = Rot90,
        FlipH = FlipH, FlipV = FlipV, Straighten = Straighten, Crop = Crop,
    };

    public bool SameAs(EditState o) =>
        Filter == o.Filter && Math.Abs(FilterAmount - o.FilterAmount) < 1e-6 && Rot90 == o.Rot90 && FlipH == o.FlipH &&
        FlipV == o.FlipV && Math.Abs(Straighten - o.Straighten) < 1e-6 && Crop == o.Crop &&
        Adj.Keys.Union(o.Adj.Keys).All(k => Math.Abs(A(k) - o.A(k)) < 1e-6);
}

public static class PhotoProcessor
{
    public static readonly (string key, string label, double min, double max)[] Adjustments =
    [
        ("exposure", L.T("Esposizione"), -1, 1), ("brilliance", L.T("Brillantezza"), -1, 1), ("highlights", L.T("Luci"), -1, 1),
        ("shadows", L.T("Ombre"), -1, 1), ("contrast", L.T("Contrasto"), -1, 1), ("brightness", L.T("Luminosità"), -1, 1),
        ("blackpoint", L.T("Punto di nero"), -1, 1), ("saturation", L.T("Saturazione"), -1, 1), ("vibrance", L.T("Vividezza"), -1, 1),
        ("warmth", L.T("Calore"), -1, 1), ("tint", L.T("Tinta"), -1, 1), ("sharpness", L.T("Nitidezza"), 0, 1),
        ("definition", L.T("Definizione"), -1, 1), ("noise", L.T("Riduzione rumore"), 0, 1), ("vignette", L.T("Vignettatura"), -1, 1),
    ];

    public static readonly (string key, string label)[] Filters =
    [
        ("none", L.T("Originale")), ("vivid", L.T("Vivido")), ("vivid_warm", L.T("Vivido caldo")), ("vivid_cool", L.T("Vivido freddo")),
        ("dramatic", L.T("Drammatico")), ("dramatic_warm", L.T("Drammatico caldo")), ("dramatic_cool", L.T("Drammatico freddo")),
        ("mono", L.T("Mono")), ("silvertone", L.T("Argento")), ("noir", L.T("Noir")),
    ];

    private static readonly string[] ToneKeys = ["exposure", "brilliance", "highlights", "shadows", "contrast", "brightness", "blackpoint"];

    // ================================================================ geometria
    public static Bgra Downscale(Bgra src, int maxSide)
    {
        var s = Math.Min(1.0, maxSide / (double)Math.Max(src.W, src.H));
        if (s >= 1) return src;
        var bmp = new TransformedBitmap(src.ToBitmap(), new ScaleTransform(s, s));
        return Bgra.FromBitmap(bmp);
    }

    public static Bgra ApplyGeometry(Bgra img, EditState st, bool crop = true)
    {
        for (var i = 0; i < ((st.Rot90 % 4) + 4) % 4; i++) img = RotateCcw(img);
        if (st.FlipH || st.FlipV) img = Flip(img, st.FlipH, st.FlipV);
        if (Math.Abs(st.Straighten) > 1e-3) img = StraightenImg(img, st.Straighten);
        if (crop && st.Crop != new Rect(0, 0, 1, 1)) img = CropImg(img, st.Crop);
        return img;
    }

    private static Bgra RotateCcw(Bgra s)
    {
        var d = new Bgra(s.H, s.W);
        Parallel.For(0, d.H, y =>
        {
            for (var x = 0; x < d.W; x++)
            {
                var si = (x * s.W + (s.W - 1 - y)) * 4;
                var di = (y * d.W + x) * 4;
                Buffer.BlockCopy(s.Px, si, d.Px, di, 4);
            }
        });
        return d;
    }

    private static Bgra Flip(Bgra s, bool h, bool v)
    {
        var d = new Bgra(s.W, s.H);
        Parallel.For(0, s.H, y =>
        {
            var sy = v ? s.H - 1 - y : y;
            for (var x = 0; x < s.W; x++)
            {
                var sx = h ? s.W - 1 - x : x;
                Buffer.BlockCopy(s.Px, (sy * s.W + sx) * 4, d.Px, (y * d.W + x) * 4, 4);
            }
        });
        return d;
    }

    /// <summary>Ruota di un angolo libero e ritaglia per non lasciare angoli vuoti (come iPhone).</summary>
    private static Bgra StraightenImg(Bgra s, double angle)
    {
        var a = Math.Abs(angle) * Math.PI / 180;
        var k = Math.Cos(a) + Math.Max(s.W / (double)s.H, s.H / (double)s.W) * Math.Sin(a);
        var w = Math.Max(1, (int)(s.W / k));
        var h = Math.Max(1, (int)(s.H / k));
        var d = new Bgra(w, h);
        var th = angle * Math.PI / 180;
        double cos = Math.Cos(th), sin = Math.Sin(th);
        double cx = s.W / 2.0, cy = s.H / 2.0;
        Parallel.For(0, h, y =>
        {
            var dy = y + 0.5 - h / 2.0;
            for (var x = 0; x < w; x++)
            {
                var dx = x + 0.5 - w / 2.0;
                var sx = cx + dx * cos + dy * sin - 0.5;
                var sy = cy - dx * sin + dy * cos - 0.5;
                Sample(s, sx, sy, d.Px, (y * w + x) * 4);
            }
        });
        return d;
    }

    private static void Sample(Bgra s, double x, double y, byte[] dst, int di)
    {
        x = Math.Clamp(x, 0, s.W - 1.001);
        y = Math.Clamp(y, 0, s.H - 1.001);
        var x0 = (int)x;
        var y0 = (int)y;
        var fx = x - x0;
        var fy = y - y0;
        var i00 = (y0 * s.W + x0) * 4;
        var i10 = i00 + 4;
        var i01 = i00 + s.W * 4;
        var i11 = i01 + 4;
        for (var c = 0; c < 4; c++)
        {
            var top = s.Px[i00 + c] * (1 - fx) + s.Px[i10 + c] * fx;
            var bot = s.Px[i01 + c] * (1 - fx) + s.Px[i11 + c] * fx;
            dst[di + c] = (byte)(top * (1 - fy) + bot * fy + 0.5);
        }
    }

    private static Bgra CropImg(Bgra s, Rect r)
    {
        var x0 = Math.Clamp((int)Math.Round(r.X * s.W), 0, s.W - 1);
        var y0 = Math.Clamp((int)Math.Round(r.Y * s.H), 0, s.H - 1);
        var x1 = Math.Clamp((int)Math.Round(r.Right * s.W), x0 + 1, s.W);
        var y1 = Math.Clamp((int)Math.Round(r.Bottom * s.H), y0 + 1, s.H);
        var d = new Bgra(x1 - x0, y1 - y0);
        for (var y = 0; y < d.H; y++)
            Buffer.BlockCopy(s.Px, ((y0 + y) * s.W + x0) * 4, d.Px, y * d.W * 4, d.W * 4);
        return d;
    }

    // ================================================================ toni
    private static double Smooth(double e0, double e1, double x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static double Curve(double y, Func<string, double> p)
    {
        var br = p("brilliance");
        var ex = p("exposure");
        var hi = p("highlights") - 0.5 * br;
        var sh = p("shadows") + 0.6 * br;
        var co = p("contrast") + 0.15 * br;
        var bright = p("brightness");
        var bp = p("blackpoint");
        if (ex != 0) y *= Math.Pow(2, ex * 1.5);
        if (hi != 0) y *= 1 + hi * 0.35 * Smooth(0.4, 1.0, y);
        if (sh != 0) y *= 1 + sh * 0.8 * (1 - Smooth(0, 0.6, y));
        if (co != 0)
        {
            var yc = Math.Clamp(y, 0, 1);
            y = co > 0 ? y + co * 0.7 * (yc * yc * (3 - 2 * yc) - yc) : 0.5 + (y - 0.5) * (1 + 0.6 * co);
        }
        if (bright != 0) y = Math.Pow(Math.Max(y, 0), Math.Pow(2, -0.6 * bright));
        if (bp != 0)
        {
            var b = 0.12 * bp;
            y = (y - b) / (1 - b);
        }
        return Math.Max(y, 0);
    }

    public static float[] BuildLut(Func<string, double> p)
    {
        var lut = new float[1024];
        for (var i = 0; i < 1024; i++) lut[i] = (float)Curve(i / 1023.0, p);
        return lut;
    }

    private static Func<string, double> P(params (string k, double v)[] vals) => k => vals.FirstOrDefault(x => x.k == k).v;

    private static readonly float[] LutVivid = BuildLut(P(("contrast", 0.15)));
    private static readonly float[] LutDramatic = BuildLut(P(("contrast", 0.45), ("shadows", -0.15), ("exposure", -0.05)));
    private static readonly float[] LutMono = BuildLut(P(("contrast", 0.1)));
    private static readonly float[] LutSilver = BuildLut(P(("contrast", 0.25), ("brightness", 0.15)));
    private static readonly float[] LutNoir = BuildLut(P(("contrast", 1.0), ("blackpoint", 0.5), ("highlights", 0.2)));

    private const float Lr = 0.2126f, Lg = 0.7152f, Lb = 0.0722f;

    private static void ApplyLut(ref float r, ref float g, ref float b, float[] lut)
    {
        var l = Lr * r + Lg * g + Lb * b;
        var idx = (int)(Math.Clamp(l, 0f, 1f) * 1023 + 0.5f);
        var ratio = (lut[idx] + 1e-3f) / (l + 1e-3f);
        r *= ratio;
        g *= ratio;
        b *= ratio;
    }

    private static void Saturate(ref float r, ref float g, ref float b, float amount)
    {
        var l = Lr * r + Lg * g + Lb * b;
        r = l + (r - l) * (1 + amount);
        g = l + (g - l) * (1 + amount);
        b = l + (b - l) * (1 + amount);
    }

    private static void Warm(ref float r, ref float b, float w)
    {
        r *= 1 + 0.12f * w;
        b *= 1 - 0.12f * w;
    }

    private static void ApplyFilter(string name, ref float r, ref float g, ref float b)
    {
        r = Math.Clamp(r, 0, 1);
        g = Math.Clamp(g, 0, 1);
        b = Math.Clamp(b, 0, 1);
        if (name.StartsWith("vivid"))
        {
            ApplyLut(ref r, ref g, ref b, LutVivid);
            Saturate(ref r, ref g, ref b, 0.3f);
            if (name.EndsWith("warm")) Warm(ref r, ref b, 0.3f);
            else if (name.EndsWith("cool")) Warm(ref r, ref b, -0.3f);
        }
        else if (name.StartsWith("dramatic"))
        {
            ApplyLut(ref r, ref g, ref b, LutDramatic);
            Saturate(ref r, ref g, ref b, -0.2f);
            if (name.EndsWith("warm")) Warm(ref r, ref b, 0.35f);
            else if (name.EndsWith("cool")) Warm(ref r, ref b, -0.35f);
        }
        else
        {
            var l = Lr * r + Lg * g + Lb * b;
            r = g = b = l;
            ApplyLut(ref r, ref g, ref b, name switch { "mono" => LutMono, "silvertone" => LutSilver, _ => LutNoir });
            if (name == "silvertone") Warm(ref r, ref b, -0.06f);
        }
    }

    /// <summary>Applica regolazioni e filtro a un'immagine con geometria già applicata.</summary>
    public static Bgra Render(Bgra src, EditState st)
    {
        var tone = ToneKeys.Any(k => Math.Abs(st.A(k)) > 1e-4);
        var lut = tone ? BuildLut(st.A) : null;
        var sat = (float)st.A("saturation");
        var vib = (float)st.A("vibrance");
        var warm = (float)st.A("warmth");
        var tint = (float)st.A("tint");
        var vig = (float)st.A("vignette");
        var filter = st.Filter;
        var amount = (float)st.FilterAmount;
        var hasFilter = filter != "none" && amount > 0;
        var any = tone || sat != 0 || vib != 0 || warm != 0 || tint != 0 || vig != 0 || hasFilter;
        var dst = new Bgra(src.W, src.H, (byte[])src.Px.Clone());
        if (any)
        {
            int W = src.W, H = src.H;
            Parallel.For(0, H, y =>
            {
                var ny = (y + 0.5f) / H * 2 - 1;
                var row = y * W * 4;
                for (var x = 0; x < W; x++)
                {
                    var i = row + x * 4;
                    float b = src.Px[i] / 255f, g = src.Px[i + 1] / 255f, r = src.Px[i + 2] / 255f;
                    if (lut != null) ApplyLut(ref r, ref g, ref b, lut);
                    if (sat != 0) Saturate(ref r, ref g, ref b, sat);
                    if (vib != 0)
                    {
                        var s = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                        Saturate(ref r, ref g, ref b, vib * Math.Clamp(1 - s, 0, 1) * 1.2f);
                    }
                    if (warm != 0) Warm(ref r, ref b, warm);
                    if (tint != 0)
                    {
                        g *= 1 - 0.10f * tint;
                        r *= 1 + 0.03f * tint;
                        b *= 1 + 0.03f * tint;
                    }
                    if (hasFilter)
                    {
                        float fr = r, fg = g, fb = b;
                        ApplyFilter(filter, ref fr, ref fg, ref fb);
                        r += amount * (fr - r);
                        g += amount * (fg - g);
                        b += amount * (fb - b);
                    }
                    if (vig != 0)
                    {
                        var nx = (x + 0.5f) / W * 2 - 1;
                        var d = MathF.Sqrt(nx * nx + ny * ny) / MathF.Sqrt(2);
                        var m = (float)Smooth(0.3, 1.0, d);
                        if (vig > 0)
                        {
                            var k = 1 - vig * 0.8f * m;
                            r *= k;
                            g *= k;
                            b *= k;
                        }
                        else
                        {
                            r += -vig * 0.6f * m * (1 - r);
                            g += -vig * 0.6f * m * (1 - g);
                            b += -vig * 0.6f * m * (1 - b);
                        }
                    }
                    dst.Px[i] = (byte)(Math.Clamp(b, 0, 1) * 255 + 0.5f);
                    dst.Px[i + 1] = (byte)(Math.Clamp(g, 0, 1) * 255 + 0.5f);
                    dst.Px[i + 2] = (byte)(Math.Clamp(r, 0, 1) * 255 + 0.5f);
                }
            });
        }
        Detail(dst, st);
        return dst;
    }

    // ================================================================ dettaglio (nitidezza, definizione, rumore)
    private static void Detail(Bgra img, EditState st)
    {
        var scale = Math.Max(img.W, img.H) / 2000.0;
        var nr = st.A("noise");
        var def = st.A("definition");
        var sharp = st.A("sharpness");
        if (nr > 0) Blend(img, Blur(img, Math.Max(1, (int)Math.Round(1.4 * scale))), Math.Min(1, nr * 0.8));
        if (def > 0) Unsharp(img, Blur(img, Math.Max(2, (int)Math.Round(25 * scale))), def * 0.7, 0);
        else if (def < 0) Blend(img, Blur(img, Math.Max(2, (int)Math.Round(12 * scale))), -def * 0.35);
        if (sharp > 0) Unsharp(img, Blur(img, Math.Max(1, (int)Math.Round(1.6 * scale))), sharp * 1.7, 2);
    }

    private static void Blend(Bgra img, byte[] other, double t)
    {
        Parallel.For(0, img.H, y =>
        {
            for (int i = y * img.W * 4, end = i + img.W * 4; i < end; i++)
            {
                if ((i & 3) == 3) continue;
                img.Px[i] = (byte)(img.Px[i] + (other[i] - img.Px[i]) * t + 0.5);
            }
        });
    }

    private static void Unsharp(Bgra img, byte[] blur, double amount, int threshold)
    {
        Parallel.For(0, img.H, y =>
        {
            for (int i = y * img.W * 4, end = i + img.W * 4; i < end; i++)
            {
                if ((i & 3) == 3) continue;
                var diff = img.Px[i] - blur[i];
                if (Math.Abs(diff) < threshold) continue;
                img.Px[i] = (byte)Math.Clamp(img.Px[i] + diff * amount, 0, 255);
            }
        });
    }

    /// <summary>Sfocatura approssimata gaussiana: tre passate di box blur separabili.</summary>
    private static byte[] Blur(Bgra img, int radius)
    {
        var a = (byte[])img.Px.Clone();
        var b = new byte[a.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxH(a, b, img.W, img.H, radius);
            BoxV(b, a, img.W, img.H, radius);
        }
        return a;
    }

    private static void BoxH(byte[] src, byte[] dst, int w, int h, int r)
    {
        Parallel.For(0, h, y =>
        {
            var row = y * w * 4;
            for (var c = 0; c < 3; c++)
            {
                var sum = 0;
                for (var x = -r; x <= r; x++) sum += src[row + Math.Clamp(x, 0, w - 1) * 4 + c];
                var n = 2 * r + 1;
                for (var x = 0; x < w; x++)
                {
                    dst[row + x * 4 + c] = (byte)(sum / n);
                    sum += src[row + Math.Min(x + r + 1, w - 1) * 4 + c] - src[row + Math.Max(x - r, 0) * 4 + c];
                }
            }
            for (var x = 0; x < w; x++) dst[row + x * 4 + 3] = src[row + x * 4 + 3];
        });
    }

    private static void BoxV(byte[] src, byte[] dst, int w, int h, int r)
    {
        Parallel.For(0, w, x =>
        {
            for (var c = 0; c < 3; c++)
            {
                var sum = 0;
                for (var y = -r; y <= r; y++) sum += src[(Math.Clamp(y, 0, h - 1) * w + x) * 4 + c];
                var n = 2 * r + 1;
                for (var y = 0; y < h; y++)
                {
                    dst[(y * w + x) * 4 + c] = (byte)(sum / n);
                    sum += src[(Math.Min(y + r + 1, h - 1) * w + x) * 4 + c] - src[(Math.Max(y - r, 0) * w + x) * 4 + c];
                }
            }
            for (var y = 0; y < h; y++) dst[(y * w + x) * 4 + 3] = src[(y * w + x) * 4 + 3];
        });
    }

    // ================================================================ automatico
    /// <summary>Stima delle regolazioni automatiche (bacchetta magica) dall'istogramma della luminanza.</summary>
    public static Dictionary<string, double> Auto(Bgra img)
    {
        var small = Downscale(img, 400);
        var hist = new int[256];
        double satSum = 0;
        var n = small.W * small.H;
        for (var i = 0; i < n; i++)
        {
            int b = small.Px[i * 4], g = small.Px[i * 4 + 1], r = small.Px[i * 4 + 2];
            hist[(int)(Lr * r + Lg * g + Lb * b)]++;
            satSum += (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b))) / 255.0;
        }
        double Pct(double p)
        {
            var target = p * n;
            var acc = 0;
            for (var i = 0; i < 256; i++)
            {
                acc += hist[i];
                if (acc >= target) return i / 255.0;
            }
            return 1;
        }
        double p2 = Pct(0.02), p50 = Pct(0.5), p98 = Pct(0.98);
        var adj = new Dictionary<string, double>
        {
            ["exposure"] = Math.Clamp(Math.Log2(0.46 / Math.Max(p50, 0.02)) / 1.5 * 0.55, -0.5, 0.5),
            ["contrast"] = Math.Clamp((0.75 - (p98 - p2)) * 0.6, -0.1, 0.3),
            ["brilliance"] = 0.2,
            ["vibrance"] = Math.Clamp((0.25 - satSum / n) * 1.5, 0, 0.35),
        };
        if (p98 > 0.96) adj["highlights"] = -0.35;
        if (p2 < 0.02) adj["shadows"] = 0.25;
        else if (p2 > 0.08) adj["blackpoint"] = Math.Clamp(p2 * 2.5, 0, 0.4);
        return adj.Where(kv => Math.Abs(kv.Value) > 0.01).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3));
    }
}
