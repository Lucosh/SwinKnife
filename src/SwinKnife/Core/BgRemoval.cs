using System.Net.Http;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SwinKnife.Core;

public enum BgMode { Transparent, Color, Blur }

/// <summary>
/// Rimozione dello sfondo con un modello di intelligenza artificiale che gira sul PC (U²-Net "silueta", ~44 MB,
/// scaricato al primo uso): stima quali pixel sono il soggetto e rende trasparente il resto.
/// </summary>
public static class BgRemoval
{
    private const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/silueta.onnx";
    private const long ModelSize = 44_173_029;
    private const int Side = 320;
    public static readonly string ModelPath = Path.Combine(AppInfo.DataDir, "models", "silueta.onnx");
    private static InferenceSession? _session;
    private static readonly object Lock = new();

    public static bool ModelReady => File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelSize;

    public static async Task DownloadModelAsync(IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        using var resp = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var tmp = ModelPath + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress.Report(done / (double)ModelSize);
            }
        }
        if (new FileInfo(tmp).Length != ModelSize)
        {
            File.Delete(tmp);
            throw new InvalidDataException(L.T("Il modello scaricato è incompleto: riprova."));
        }
        File.Move(tmp, ModelPath, true);
    }

    private static InferenceSession Session()
    {
        lock (Lock)
        {
            if (_session != null) return _session;
            if (!ModelReady) throw new InvalidOperationException(L.T("Il modello per la rimozione dello sfondo non è ancora stato scaricato."));
            var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
            return _session = new InferenceSession(ModelPath, opts);
        }
    }

    /// <summary>Maschera del soggetto (0 = sfondo, 255 = soggetto) alla risoluzione dell'immagine.</summary>
    public static MagickImage Mask(MagickImage img)
    {
        using var small = (MagickImage)img.Clone();
        small.Alpha(AlphaOption.Remove);
        small.Resize(new MagickGeometry(Side, Side) { IgnoreAspectRatio = true });
        var rgb = small.ToByteArray(MagickFormat.Rgb);
        var input = new DenseTensor<float>([1, 3, Side, Side]);
        float[] mean = [0.485f, 0.456f, 0.406f], std = [0.229f, 0.224f, 0.225f];
        float max = 1;
        foreach (var b in rgb) max = Math.Max(max, b);
        for (var y = 0; y < Side; y++)
        for (var x = 0; x < Side; x++)
        {
            var i = (y * Side + x) * 3;
            for (var c = 0; c < 3; c++) input[0, c, y, x] = (rgb[i + c] / max - mean[c]) / std[c];
        }
        var session = Session();
        float[] pred;
        lock (Lock)
        {
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input)]);
            pred = results.First().AsEnumerable<float>().Take(Side * Side).ToArray();
        }
        float lo = pred.Min(), hi = pred.Max();
        var bytes = new byte[Side * Side];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)Math.Clamp((pred[i] - lo) / Math.Max(1e-6f, hi - lo) * 255, 0, 255);
        var mask = new MagickImage(bytes, new PixelReadSettings(Side, Side, StorageType.Char, "I")); // "I" = scala di grigi
        mask.FilterType = FilterType.Lanczos;
        mask.Resize(new MagickGeometry(img.Width, img.Height) { IgnoreAspectRatio = true });
        return mask;
    }

    /// <summary>Immagine col soggetto ritagliato sullo sfondo scelto.</summary>
    public static MagickImage Apply(MagickImage img, MagickImage mask, BgMode mode, MagickColor? color = null)
    {
        var cut = (MagickImage)img.Clone();
        cut.Alpha(AlphaOption.Set);
        cut.Composite(mask, CompositeOperator.CopyAlpha);
        if (mode == BgMode.Transparent) return cut;
        MagickImage bg;
        if (mode == BgMode.Blur)
        {
            // sfocatura forte calcolata su una copia ridotta (molto più veloce, il risultato è lo stesso)
            bg = (MagickImage)img.Clone();
            bg.Alpha(AlphaOption.Remove);
            bg.Resize(new MagickGeometry(Math.Max(1, img.Width / 8), Math.Max(1, img.Height / 8)) { IgnoreAspectRatio = true });
            bg.Blur(0, Math.Max(bg.Width, bg.Height) / 60.0);
            bg.Resize(new MagickGeometry(img.Width, img.Height) { IgnoreAspectRatio = true });
        }
        else bg = new MagickImage(color ?? MagickColors.White, img.Width, img.Height);
        bg.Composite(cut, CompositeOperator.Over);
        bg.Alpha(AlphaOption.Off);
        cut.Dispose();
        return bg;
    }
}
