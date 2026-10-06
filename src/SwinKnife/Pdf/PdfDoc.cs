using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static SwinKnife.Pdf.Native;

namespace SwinKnife.Pdf;

public sealed class PdfPasswordException(string message) : Exception(message);

/// <summary>Riga di testo modificabile: uno o più oggetti di testo sulla stessa linea di base.</summary>
public sealed class TextLine
{
    public required string Text { get; init; }
    public required Rect Bounds { get; init; }          // coordinate PDF (y verso l'alto)
    public required List<Rect> ObjectBounds { get; init; }
    public required FS_MATRIX Matrix { get; init; }
    public required float FontSize { get; init; }
    public required string FontName { get; init; }
    public required Color Color { get; init; }
    public bool Bold => FontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) || FontName.Contains("Black", StringComparison.OrdinalIgnoreCase);
    public bool Italic => FontName.Contains("Italic", StringComparison.OrdinalIgnoreCase) || FontName.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
    public int Family
    {
        get
        {
            var n = FontName.ToLowerInvariant();
            if (new[] { "times", "serif", "roman", "georgia", "cambria", "garamond", "book" }.Any(n.Contains) && !n.Contains("sans")) return 1;
            if (new[] { "courier", "mono", "consol" }.Any(n.Contains)) return 2;
            return 0;
        }
    }
}

/// <summary>Documento PDF gestito con PDFium. Non thread-safe: usare da un solo thread alla volta.</summary>
public sealed unsafe class PdfDoc : IDisposable
{
    public static readonly string[] StandardFamilies = ["Helvetica", "Times", "Courier"];

    /// <summary>PDFium non è thread-safe: ogni accesso passa da qui (il lock è rientrante).</summary>
    public static readonly object Gate = new();

    private IntPtr _doc;
    private IntPtr _buffer;
    private IntPtr _formInfo;
    private IntPtr _form;

    private PdfDoc(IntPtr doc, IntPtr buffer)
    {
        _doc = doc;
        _buffer = buffer;
        // ambiente moduli: serve per disegnare i campi compilabili (con i valori inseriti)
        _formInfo = Marshal.AllocHGlobal(1024);
        new Span<byte>((void*)_formInfo, 1024).Clear();
        Marshal.WriteInt32(_formInfo, 1);
        _form = FPDFDOC_InitFormFillEnvironment(doc, _formInfo);
    }

    public static PdfDoc Open(string path, string? password = null) => Open(File.ReadAllBytes(path), password);

    public static PdfDoc Open(byte[] data, string? password = null)
    {
        lock (Gate) return OpenCore(data, password);
    }

    private static PdfDoc OpenCore(byte[] data, string? password)
    {
        EnsureInit();
        var buf = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, buf, data.Length);
        var doc = FPDF_LoadMemDocument64(buf, (nuint)data.Length, password);
        if (doc == IntPtr.Zero)
        {
            var err = FPDF_GetLastError();
            Marshal.FreeHGlobal(buf);
            if (err == FPDF_ERR_PASSWORD)
                throw new PdfPasswordException(password == null ? L.T("Il documento è protetto da password.") : L.T("Password errata."));
            throw new InvalidDataException(err switch
            {
                2 => L.T("File non trovato o non leggibile."),
                3 => L.T("Il file non è un PDF valido o è danneggiato."),
                6 => L.T("Pagina non trovata."),
                _ => L.T($"Impossibile aprire il PDF (errore {err})."),
            });
        }
        return new PdfDoc(doc, buf);
    }

    public static PdfDoc CreateNew()
    {
        lock (Gate)
        {
            EnsureInit();
            return new PdfDoc(FPDF_CreateNewDocument(), IntPtr.Zero);
        }
    }

    public static bool NeedsPassword(byte[] data)
    {
        try
        {
            using var d = Open(data);
            return false;
        }
        catch (PdfPasswordException)
        {
            return true;
        }
    }

    public int PageCount
    {
        get { lock (Gate) return FPDF_GetPageCount(_doc); }
    }

    public void Dispose()
    {
        lock (Gate) DisposeCore();
    }

    private void DisposeCore()
    {
        if (_form != IntPtr.Zero)
        {
            FPDFDOC_ExitFormFillEnvironment(_form);
            _form = IntPtr.Zero;
        }
        if (_formInfo != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_formInfo);
            _formInfo = IntPtr.Zero;
        }
        if (_doc != IntPtr.Zero)
        {
            FPDF_CloseDocument(_doc);
            _doc = IntPtr.Zero;
        }
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    // ================================================================ accesso alle pagine
    private readonly struct PageHandle : IDisposable
    {
        public readonly IntPtr Page;
        public PageHandle(IntPtr doc, int index)
        {
            Monitor.Enter(Gate);
            Page = FPDF_LoadPage(doc, index);
            if (Page == IntPtr.Zero)
            {
                Monitor.Exit(Gate);
                throw new ArgumentOutOfRangeException(nameof(index), L.T("Pagina non valida"));
            }
        }
        public void Dispose()
        {
            FPDF_ClosePage(Page);
            Monitor.Exit(Gate);
        }
    }

    private PageHandle Load(int index) => new(_doc, index);

    /// <summary>Dimensioni visualizzate (rotazione già applicata), in punti.</summary>
    public Size PageSize(int index)
    {
        using var p = Load(index);
        return new Size(FPDF_GetPageWidthF(p.Page), FPDF_GetPageHeightF(p.Page));
    }

    /// <summary>Rotazione in quarti di giro orari (0..3).</summary>
    public int Rotation(int index)
    {
        using var p = Load(index);
        return FPDFPage_GetRotation(p.Page);
    }

    public void SetRotation(int index, int quarterTurns)
    {
        using var p = Load(index);
        FPDFPage_SetRotation(p.Page, ((quarterTurns % 4) + 4) % 4);
    }

    // ================================================================ rendering
    public BitmapSource Render(int index, double scale, bool annotations = true)
    {
        using var p = Load(index);
        var w = Math.Max(1, (int)Math.Round(FPDF_GetPageWidthF(p.Page) * scale));
        var h = Math.Max(1, (int)Math.Round(FPDF_GetPageHeightF(p.Page) * scale));
        var stride = w * 4;
        var pixels = new byte[stride * h];
        fixed (byte* ptr = pixels)
        {
            var bmp = FPDFBitmap_CreateEx(w, h, FPDFBitmap_BGRA, (IntPtr)ptr, stride);
            FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
            FPDF_RenderPageBitmap(bmp, p.Page, 0, 0, w, h, 0, (annotations ? FPDF_ANNOT : 0) | FPDF_LCD_TEXT);
            if (annotations) DrawForm(p.Page, bmp, w, h, FPDF_ANNOT | FPDF_LCD_TEXT);
            FPDFBitmap_Destroy(bmp);
        }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        src.Freeze();
        return src;
    }

    /// <summary>Rendering in un buffer BGRA (per conversioni in immagini).</summary>
    public (byte[] pixels, int width, int height) RenderRaw(int index, double scale)
    {
        using var p = Load(index);
        var w = Math.Max(1, (int)Math.Round(FPDF_GetPageWidthF(p.Page) * scale));
        var h = Math.Max(1, (int)Math.Round(FPDF_GetPageHeightF(p.Page) * scale));
        var pixels = new byte[w * 4 * h];
        fixed (byte* ptr = pixels)
        {
            var bmp = FPDFBitmap_CreateEx(w, h, FPDFBitmap_BGRA, (IntPtr)ptr, w * 4);
            FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
            FPDF_RenderPageBitmap(bmp, p.Page, 0, 0, w, h, 0, FPDF_ANNOT | FPDF_PRINTING);
            DrawForm(p.Page, bmp, w, h, FPDF_ANNOT | FPDF_PRINTING);
            FPDFBitmap_Destroy(bmp);
        }
        return (pixels, w, h);
    }

    private void DrawForm(IntPtr page, IntPtr bmp, int w, int h, int flags)
    {
        if (_form == IntPtr.Zero) return;
        FORM_OnAfterLoadPage(page, _form);
        FPDF_FFLDraw(_form, bmp, page, 0, 0, w, h, 0, flags);
        FORM_OnBeforeClosePage(page, _form);
    }

    // ================================================================ coordinate
    private const int K = 20; // risoluzione virtuale per conversioni precise

    /// <summary>Da punto visualizzato (punti, y in basso) a coordinate PDF (y in alto, pagina non ruotata).</summary>
    public Point ToPdf(int index, Point display)
    {
        using var p = Load(index);
        return ToPdf(p.Page, display);
    }

    private static Point ToPdf(IntPtr page, Point display)
    {
        var w = (int)(FPDF_GetPageWidthF(page) * K);
        var h = (int)(FPDF_GetPageHeightF(page) * K);
        FPDF_DeviceToPage(page, 0, 0, w, h, 0, (int)Math.Round(display.X * K), (int)Math.Round(display.Y * K), out var x, out var y);
        return new Point(x, y);
    }

    private static Point ToDisplay(IntPtr page, double x, double y)
    {
        var w = (int)(FPDF_GetPageWidthF(page) * K);
        var h = (int)(FPDF_GetPageHeightF(page) * K);
        FPDF_PageToDevice(page, 0, 0, w, h, 0, x, y, out var dx, out var dy);
        return new Point(dx / (double)K, dy / (double)K);
    }

    private static Rect ToDisplay(IntPtr page, Rect pdf) =>
        new(ToDisplay(page, pdf.Left, pdf.Top), ToDisplay(page, pdf.Right, pdf.Bottom));

    public Rect ToDisplay(int index, Rect pdf)
    {
        using var p = Load(index);
        return ToDisplay(p.Page, pdf);
    }

    public Rect ToPdf(int index, Rect display)
    {
        using var p = Load(index);
        return new Rect(ToPdf(p.Page, display.TopLeft), ToPdf(p.Page, display.BottomRight));
    }

    /// <summary>Matrice di rotazione che fa apparire dritto un oggetto su una pagina ruotata.</summary>
    private static (double cos, double sin) Upright(IntPtr page)
    {
        var r = FPDFPage_GetRotation(page);
        var angle = r * Math.PI / 2;
        return (Math.Round(Math.Cos(angle)), Math.Round(Math.Sin(angle)));
    }

    // ================================================================ salvataggio e struttura
    public byte[] Save(bool removeSecurity = true)
    {
        lock (Gate) return SaveCore(removeSecurity);
    }

    private byte[] SaveCore(bool removeSecurity)
    {
        using var ms = new MemoryStream();
        WriteBlockDelegate cb = (_, data, size) =>
        {
            var tmp = new byte[size];
            Marshal.Copy(data, tmp, 0, (int)size);
            ms.Write(tmp, 0, tmp.Length);
            return 1;
        };
        var fw = new FPDF_FILEWRITE { version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(cb) };
        var ok = FPDF_SaveAsCopy(_doc, ref fw, removeSecurity ? (uint)FPDF_REMOVE_SECURITY : 0);
        GC.KeepAlive(cb);
        if (ok == 0) throw new IOException(L.T("Salvataggio del PDF non riuscito."));
        return ms.ToArray();
    }

    public void DeletePage(int index)
    {
        lock (Gate) FPDFPage_Delete(_doc, index);
    }

    public void InsertBlankPage(int index, double width, double height)
    {
        lock (Gate)
        {
            var p = FPDFPage_New(_doc, index, width, height);
            if (p != IntPtr.Zero) FPDF_ClosePage(p);
        }
    }

    /// <summary>Copia pagine da un altro documento (tutte se indices è null) alla posizione indicata.</summary>
    public void Import(PdfDoc src, int at, int[]? indices = null)
    {
        lock (Gate) ImportCore(src, at, indices);
    }

    private void ImportCore(PdfDoc src, int at, int[]? indices)
    {
        int ok;
        if (indices == null)
        {
            ok = FPDF_ImportPages(_doc, src._doc, null, at);
        }
        else
        {
            fixed (int* ptr = indices) ok = FPDF_ImportPagesByIndex(_doc, src._doc, ptr, (uint)indices.Length, at);
        }
        if (ok == 0) throw new InvalidOperationException(L.T("Impossibile importare le pagine."));
    }

    /// <summary>Riordina le pagine: newOrder[i] = indice originale della pagina che va in posizione i.</summary>
    public void Reorder(int[] newOrder)
    {
        lock (Gate) ReorderCore(newOrder);
    }

    private void ReorderCore(int[] newOrder)
    {
        fixed (int* ptr = newOrder)
        {
            if (FPDF_MovePages(_doc, ptr, (uint)newOrder.Length, 0) == 0)
                throw new InvalidOperationException(L.T("Riordino delle pagine non riuscito."));
        }
    }

    public PdfDoc ExtractPages(int[] indices)
    {
        var d = CreateNew();
        d.Import(this, 0, indices);
        return d;
    }

    // ================================================================ testo
    public string PageText(int index)
    {
        using var p = Load(index);
        var tp = FPDFText_LoadPage(p.Page);
        try
        {
            var n = FPDFText_CountChars(tp);
            if (n <= 0) return "";
            var buf = new char[n + 1];
            fixed (char* b = buf) FPDFText_GetText(tp, 0, n, b);
            return new string(buf, 0, n).Replace("\r\n", "\n").Replace('\r', '\n');
        }
        finally
        {
            FPDFText_ClosePage(tp);
        }
    }

    /// <summary>Rettangoli (in coordinate visualizzate) delle occorrenze del testo cercato.</summary>
    public List<Rect> Search(int index, string what)
    {
        var result = new List<Rect>();
        using var p = Load(index);
        var tp = FPDFText_LoadPage(p.Page);
        var h = FPDFText_FindStart(tp, what, 0, 0);
        try
        {
            while (FPDFText_FindNext(h) != 0)
            {
                var start = FPDFText_GetSchResultIndex(h);
                var count = FPDFText_GetSchCount(h);
                var n = FPDFText_CountRects(tp, start, count);
                for (var i = 0; i < n; i++)
                {
                    FPDFText_GetRect(tp, i, out var l, out var t, out var r, out var b);
                    result.Add(ToDisplay(p.Page, new Rect(new Point(l, t), new Point(r, b))));
                }
            }
        }
        finally
        {
            FPDFText_FindClose(h);
            FPDFText_ClosePage(tp);
        }
        return result;
    }

    private sealed record TextObj(IntPtr Handle, Rect Bounds, FS_MATRIX M, string Text, float Size, string Font, Color Color);

    private static List<TextObj> TextObjects(IntPtr page, IntPtr textPage)
    {
        var list = new List<TextObj>();
        var n = FPDFPage_CountObjects(page);
        for (var i = 0; i < n; i++)
        {
            var o = FPDFPage_GetObject(page, i);
            if (FPDFPageObj_GetType(o) != FPDF_PAGEOBJ_TEXT) continue;
            FPDFPageObj_GetBounds(o, out var l, out var b, out var r, out var t);
            FPDFPageObj_GetMatrix(o, out var m);
            var len = FPDFTextObj_GetText(o, textPage, null, 0);
            var text = "";
            if (len > 2)
            {
                var buf = new char[len / 2];
                fixed (char* ptr = buf) FPDFTextObj_GetText(o, textPage, ptr, len);
                text = new string(buf).TrimEnd('\0');
            }
            FPDFTextObj_GetFontSize(o, out var size);
            var font = "";
            var f = FPDFTextObj_GetFont(o);
            if (f != IntPtr.Zero)
            {
                var nameLen = FPDFFont_GetBaseFontName(f, null, 0);
                if (nameLen > 1)
                {
                    var nb = new byte[(int)nameLen];
                    fixed (byte* ptr = nb) FPDFFont_GetBaseFontName(f, ptr, nameLen);
                    font = System.Text.Encoding.UTF8.GetString(nb, 0, nb.Length - 1);
                }
                if (FPDFFont_GetWeight(f) >= 600 && !font.Contains("Bold")) font += "-Bold";
            }
            FPDFPageObj_GetFillColor(o, out var cr, out var cg, out var cb, out _);
            list.Add(new TextObj(o, new Rect(new Point(l, b), new Point(r, t)), m, text, size, font,
                Color.FromRgb((byte)cr, (byte)cg, (byte)cb)));
        }
        return list;
    }

    /// <summary>Raggruppa gli oggetti di testo in righe modificabili.</summary>
    public List<TextLine> TextLines(int index)
    {
        using var p = Load(index);
        var tp = FPDFText_LoadPage(p.Page);
        try
        {
            var objs = TextObjects(p.Page, tp).Where(o => o.Text.Trim().Length > 0).ToList();
            var lines = new List<TextLine>();
            var horizontal = objs.Where(o => Math.Abs(o.M.b) < 1e-3 && Math.Abs(o.M.c) < 1e-3 && o.M.a > 0).ToList();
            foreach (var o in objs.Except(horizontal))
                lines.Add(MakeLine([o]));
            var used = new HashSet<TextObj>();
            foreach (var seed in horizontal.OrderByDescending(o => o.M.f).ThenBy(o => o.Bounds.Left))
            {
                if (used.Contains(seed)) continue;
                var tol = Math.Max(1.0, seed.Bounds.Height * 0.35);
                var row = horizontal.Where(o => !used.Contains(o) && Math.Abs(o.M.f - seed.M.f) <= tol)
                    .OrderBy(o => o.Bounds.Left).ToList();
                var cluster = new List<TextObj>();
                foreach (var o in row)
                {
                    if (cluster.Count > 0 && o.Bounds.Left - cluster[^1].Bounds.Right > Math.Max(o.Bounds.Height, cluster[^1].Bounds.Height) * 1.6)
                    {
                        lines.Add(MakeLine(cluster));
                        cluster = [];
                    }
                    cluster.Add(o);
                    used.Add(o);
                }
                if (cluster.Count > 0) lines.Add(MakeLine(cluster));
            }
            return lines;
        }
        finally
        {
            FPDFText_ClosePage(tp);
        }
    }

    private static TextLine MakeLine(List<TextObj> parts)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                var gap = parts[i].Bounds.Left - parts[i - 1].Bounds.Right;
                var needSpace = gap > parts[i].Bounds.Height * 0.15 && !sb.ToString().EndsWith(' ') && !parts[i].Text.StartsWith(' ');
                if (needSpace) sb.Append(' ');
            }
            sb.Append(parts[i].Text);
        }
        var bounds = parts[0].Bounds;
        foreach (var o in parts.Skip(1)) bounds.Union(o.Bounds);
        var first = parts[0];
        return new TextLine
        {
            Text = sb.ToString().TrimEnd(), Bounds = bounds, ObjectBounds = parts.Select(o => o.Bounds).ToList(),
            Matrix = first.M, FontSize = first.Size, FontName = first.Font, Color = first.Color,
        };
    }

    public Rect LineDisplayRect(int index, TextLine line) => ToDisplay(index, line.Bounds);

    public static string StandardFont(int family, bool bold, bool italic)
    {
        var baseName = StandardFamilies[Math.Clamp(family, 0, 2)];
        if (baseName == "Times")
            return bold && italic ? "Times-BoldItalic" : bold ? "Times-Bold" : italic ? "Times-Italic" : "Times-Roman";
        var oblique = baseName == "Helvetica" || baseName == "Courier";
        var suffix = bold && italic ? "-BoldOblique" : bold ? "-Bold" : italic ? (oblique ? "-Oblique" : "-Italic") : "";
        return baseName + suffix;
    }

    private static bool Same(Rect a, Rect b) =>
        Math.Abs(a.Left - b.Left) < 0.05 && Math.Abs(a.Top - b.Top) < 0.05 &&
        Math.Abs(a.Right - b.Right) < 0.05 && Math.Abs(a.Bottom - b.Bottom) < 0.05;

    /// <summary>Sostituisce una riga di testo con un nuovo testo (font standard, stessa posizione e dimensione).</summary>
    public void ReplaceLine(int index, TextLine line, string text, string font, float size, Color color)
    {
        using var p = Load(index);
        var tp = FPDFText_LoadPage(p.Page);
        try
        {
            foreach (var o in TextObjects(p.Page, tp).Where(o => line.ObjectBounds.Any(b => Same(b, o.Bounds))))
            {
                if (FPDFPage_RemoveObject(p.Page, o.Handle) != 0) FPDFPageObj_Destroy(o.Handle);
            }
        }
        finally
        {
            FPDFText_ClosePage(tp);
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            var m = line.Matrix;
            // la dimensione richiesta dall'utente è quella effettiva: tolgo la scala della matrice
            var scale = Math.Sqrt(m.a * m.a + m.b * m.b);
            var fontSize = scale > 0 ? (float)(size / scale) : size;
            var obj = FPDFPageObj_NewTextObj(_doc, font, fontSize);
            FPDFText_SetText(obj, text);
            FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, 255);
            FPDFPageObj_SetMatrix(obj, ref m);
            FPDFPage_InsertObject(p.Page, obj);
        }
        FPDFPage_GenerateContent(p.Page);
    }

    /// <summary>Aggiunge testo (anche su più righe) con l'angolo in alto a sinistra nel punto visualizzato.</summary>
    public void AddText(int index, Point display, string text, string font, float size, Color color)
    {
        using var p = Load(index);
        var (cos, sin) = Upright(p.Page);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            var pt = ToPdf(p.Page, new Point(display.X, display.Y + size * (0.85 + i * 1.25)));
            var obj = FPDFPageObj_NewTextObj(_doc, font, size);
            FPDFText_SetText(obj, lines[i]);
            FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, 255);
            FPDFPageObj_Transform(obj, cos, sin, -sin, cos, pt.X, pt.Y);
            FPDFPage_InsertObject(p.Page, obj);
        }
        FPDFPage_GenerateContent(p.Page);
    }

    /// <summary>Strato di testo invisibile (OCR) sopra una pagina: rettangoli in coordinate visualizzate.</summary>
    public void AddOcrLayer(int index, IEnumerable<(Rect display, string text)> words)
    {
        using var p = Load(index);
        var (cos, sin) = Upright(p.Page);
        foreach (var (r, text) in words)
        {
            if (string.IsNullOrWhiteSpace(text) || r.Height < 1 || r.Width < 1) continue;
            var size = (float)(r.Height * 0.95);
            var obj = FPDFPageObj_NewTextObj(_doc, "Helvetica", size);
            FPDFText_SetText(obj, text);
            FPDFTextObj_SetTextRenderMode(obj, 3); // invisibile
            FPDFPageObj_GetBounds(obj, out var l, out _, out var rr, out _);
            var sx = rr - l > 0.01 ? r.Width / (rr - l) : 1;
            var origin = ToPdf(p.Page, new Point(r.Left, r.Bottom - r.Height * 0.18));
            FPDFPageObj_Transform(obj, sx, 0, 0, 1, 0, 0);
            FPDFPageObj_Transform(obj, cos, sin, -sin, cos, origin.X, origin.Y);
            FPDFPage_InsertObject(p.Page, obj);
        }
        FPDFPage_GenerateContent(p.Page);
    }

    // ================================================================ annotazioni
    public void AddHighlight(int index, Rect display, Color color)
    {
        using var p = Load(index);
        var area = new Rect(ToPdf(p.Page, display.TopLeft), ToPdf(p.Page, display.BottomRight));
        var tp = FPDFText_LoadPage(p.Page);
        var rects = new List<Rect>();
        try
        {
            // evidenzia le righe di testo comprese nell'area, altrimenti l'area intera
            var n = FPDFText_CountRects(tp, 0, -1);
            for (var i = 0; i < n; i++)
            {
                FPDFText_GetRect(tp, i, out var l, out var t, out var r, out var b);
                var rr = new Rect(new Point(l, b), new Point(r, t));
                var inter = Rect.Intersect(rr, area);
                if (!inter.IsEmpty && inter.Height > rr.Height * 0.4)
                    rects.Add(new Rect(Math.Max(rr.Left, area.Left), rr.Top, Math.Min(rr.Right, area.Right) - Math.Max(rr.Left, area.Left), rr.Height));
            }
        }
        finally
        {
            FPDFText_ClosePage(tp);
        }
        if (rects.Count == 0) rects.Add(area);
        var annot = FPDFPage_CreateAnnot(p.Page, FPDF_ANNOT_HIGHLIGHT);
        FPDFAnnot_SetColor(annot, 0, color.R, color.G, color.B, 255);
        var bounds = rects[0];
        foreach (var r in rects)
        {
            bounds.Union(r);
            var q = new FS_QUADPOINTSF
            {
                x1 = (float)r.Left, y1 = (float)r.Bottom, x2 = (float)r.Right, y2 = (float)r.Bottom,
                x3 = (float)r.Left, y3 = (float)r.Top, x4 = (float)r.Right, y4 = (float)r.Top,
            };
            FPDFAnnot_AppendAttachmentPoints(annot, ref q);
        }
        var fr = new FS_RECTF { left = (float)bounds.Left, bottom = (float)bounds.Top, right = (float)bounds.Right, top = (float)bounds.Bottom };
        FPDFAnnot_SetRect(annot, ref fr);
        FPDFPage_CloseAnnot(annot);
    }

    public void AddInk(int index, IReadOnlyList<Point> display, Color color, float width)
    {
        using var p = Load(index);
        var pts = display.Select(d => ToPdf(p.Page, d)).ToArray();
        var annot = FPDFPage_CreateAnnot(p.Page, FPDF_ANNOT_INK);
        FPDFAnnot_SetColor(annot, 0, color.R, color.G, color.B, 255);
        FPDFAnnot_SetBorder(annot, 0, 0, width);
        var arr = pts.Select(q => new FS_POINTF { x = (float)q.X, y = (float)q.Y }).ToArray();
        fixed (FS_POINTF* ptr = arr) FPDFAnnot_AddInkStroke(annot, ptr, (nuint)arr.Length);
        var minX = pts.Min(q => q.X) - width; var maxX = pts.Max(q => q.X) + width;
        var minY = pts.Min(q => q.Y) - width; var maxY = pts.Max(q => q.Y) + width;
        var fr = new FS_RECTF { left = (float)minX, bottom = (float)minY, right = (float)maxX, top = (float)maxY };
        FPDFAnnot_SetRect(annot, ref fr);
        FPDFPage_CloseAnnot(annot);
    }

    public void AddNote(int index, Point display, string text)
    {
        using var p = Load(index);
        var pt = ToPdf(p.Page, display);
        var annot = FPDFPage_CreateAnnot(p.Page, FPDF_ANNOT_TEXT);
        var fr = new FS_RECTF { left = (float)pt.X, top = (float)pt.Y, right = (float)pt.X + 20, bottom = (float)pt.Y - 20 };
        FPDFAnnot_SetRect(annot, ref fr);
        FPDFAnnot_SetColor(annot, 0, 255, 214, 10, 255);
        FPDFAnnot_SetStringValue(annot, "Contents", text);
        FPDFAnnot_SetStringValue(annot, "T", Environment.UserName);
        FPDFPage_CloseAnnot(annot);
    }

    /// <summary>Elimina l'annotazione (non link né campi modulo) nel punto indicato.</summary>
    public bool RemoveAnnotAt(int index, Point display)
    {
        using var p = Load(index);
        var pt = ToPdf(p.Page, display);
        var n = FPDFPage_GetAnnotCount(p.Page);
        for (var i = n - 1; i >= 0; i--)
        {
            var a = FPDFPage_GetAnnot(p.Page, i);
            var type = FPDFAnnot_GetSubtype(a);
            FPDFAnnot_GetRect(a, out var r);
            FPDFPage_CloseAnnot(a);
            if (type is FPDF_ANNOT_LINK or FPDF_ANNOT_WIDGET or FPDF_ANNOT_POPUP) continue;
            var rect = new Rect(new Point(r.left, r.bottom), new Point(r.right, r.top));
            rect.Inflate(2, 2);
            if (rect.Contains(pt))
            {
                FPDFPage_RemoveAnnot(p.Page, i);
                return true;
            }
        }
        return false;
    }

    // ================================================================ contenuti
    /// <summary>
    /// Copre un'area con un rettangolo pieno rimuovendo il testo sottostante.
    /// strict = true (oscuramento) rimuove ogni oggetto che tocca l'area, anche solo in parte.
    /// </summary>
    public int Cover(int index, Rect display, Color fill, bool strict)
    {
        using var p = Load(index);
        var area = new Rect(ToPdf(p.Page, display.TopLeft), ToPdf(p.Page, display.BottomRight));
        var removed = 0;
        var n = FPDFPage_CountObjects(p.Page);
        var toRemove = new List<IntPtr>();
        for (var i = 0; i < n; i++)
        {
            var o = FPDFPage_GetObject(p.Page, i);
            var type = FPDFPageObj_GetType(o);
            if (type is not (FPDF_PAGEOBJ_TEXT or FPDF_PAGEOBJ_IMAGE)) continue;
            FPDFPageObj_GetBounds(o, out var l, out var b, out var r, out var t);
            var rb = new Rect(new Point(l, b), new Point(r, t));
            var inter = Rect.Intersect(rb, area);
            if (inter.IsEmpty) continue;
            var frac = rb.Width * rb.Height > 0 ? inter.Width * inter.Height / (rb.Width * rb.Height) : 1;
            if (type == FPDF_PAGEOBJ_TEXT ? (strict || frac >= 0.95) : frac >= 0.98)
                toRemove.Add(o);
        }
        foreach (var o in toRemove)
        {
            if (FPDFPage_RemoveObject(p.Page, o) != 0)
            {
                FPDFPageObj_Destroy(o);
                removed++;
            }
        }
        var rect = FPDFPageObj_CreateNewRect((float)area.Left, (float)area.Top, (float)area.Width, (float)area.Height);
        FPDFPageObj_SetFillColor(rect, fill.R, fill.G, fill.B, 255);
        FPDFPath_SetDrawMode(rect, FPDF_FILLMODE_ALTERNATE, 0);
        FPDFPage_InsertObject(p.Page, rect);
        FPDFPage_GenerateContent(p.Page);
        return removed;
    }

    /// <summary>Inserisce un'immagine adattandola (proporzioni mantenute) al rettangolo visualizzato.</summary>
    public void AddImage(int index, Rect display, BitmapSource image)
    {
        var bmp = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        // adatta mantenendo le proporzioni dentro l'area scelta
        var s = Math.Min(display.Width / w, display.Height / h);
        var fit = new Rect(display.X + (display.Width - w * s) / 2, display.Y + (display.Height - h * s) / 2, w * s, h * s);
        using var p = Load(index);
        var a = ToPdf(p.Page, fit.BottomLeft);
        var (cos, sin) = Upright(p.Page);
        double pdfW = fit.Width, pdfH = fit.Height;
        var obj = FPDFPageObj_NewImageObj(_doc);
        fixed (byte* ptr = px)
        {
            var fb = FPDFBitmap_CreateEx(w, h, FPDFBitmap_BGRA, (IntPtr)ptr, w * 4);
            var page = p.Page;
            FPDFImageObj_SetBitmap(&page, 1, obj, fb);
            FPDFBitmap_Destroy(fb);
        }
        // matrice: scala (larghezza, altezza) ruotata per restare dritta, origine nell'angolo in basso a sinistra visualizzato
        FPDFImageObj_SetMatrix(obj, pdfW * cos, pdfW * sin, -pdfH * sin, pdfH * cos, a.X, a.Y);
        FPDFPage_InsertObject(p.Page, obj);
        FPDFPage_GenerateContent(p.Page);
    }

    /// <summary>Scrive un testo centrato nella pagina (filigrana) con opacità e angolo dati.</summary>
    public void AddCenteredText(int index, string text, float size, Color color, byte alpha, double angleDeg, string font = "Helvetica")
    {
        using var p = Load(index);
        var dw = FPDF_GetPageWidthF(p.Page);
        var dh = FPDF_GetPageHeightF(p.Page);
        var c = ToPdf(p.Page, new Point(dw / 2, dh / 2));
        var obj = FPDFPageObj_NewTextObj(_doc, font, size);
        FPDFText_SetText(obj, text);
        FPDFPageObj_GetBounds(obj, out var l, out var b, out var r, out var t);
        FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, alpha);
        FPDFPageObj_Transform(obj, 1, 0, 0, 1, -(l + r) / 2, -(b + t) / 2);
        var ang = (angleDeg + FPDFPage_GetRotation(p.Page) * 90) * Math.PI / 180;
        FPDFPageObj_Transform(obj, Math.Cos(ang), Math.Sin(ang), -Math.Sin(ang), Math.Cos(ang), c.X, c.Y);
        FPDFPage_InsertObject(p.Page, obj);
        FPDFPage_GenerateContent(p.Page);
    }

    /// <summary>Scrive un testo centrato orizzontalmente a una certa distanza dal fondo della pagina visualizzata.</summary>
    public void AddFooterText(int index, string text, float size, Color color, double bottomMargin = 22)
    {
        using var p = Load(index);
        var dw = FPDF_GetPageWidthF(p.Page);
        var dh = FPDF_GetPageHeightF(p.Page);
        var c = ToPdf(p.Page, new Point(dw / 2, dh - bottomMargin));
        var obj = FPDFPageObj_NewTextObj(_doc, "Helvetica", size);
        FPDFText_SetText(obj, text);
        FPDFPageObj_GetBounds(obj, out var l, out _, out var r, out _);
        FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, 255);
        FPDFPageObj_Transform(obj, 1, 0, 0, 1, -(l + r) / 2, 0);
        var (cos, sin) = Upright(p.Page);
        FPDFPageObj_Transform(obj, cos, sin, -sin, cos, c.X, c.Y);
        FPDFPage_InsertObject(p.Page, obj);
        FPDFPage_GenerateContent(p.Page);
    }
}
