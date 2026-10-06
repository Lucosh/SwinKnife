using System.Runtime.InteropServices;

namespace SwinKnife.Pdf;

/// <summary>Dichiarazioni P/Invoke dell'API C di PDFium (pdfium.dll dal pacchetto bblanchon.PDFium.Win32).</summary>
public static unsafe class Native
{
    private const string Dll = "pdfium";

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_PRINTING = 0x800;
    public const int FPDFBitmap_BGRA = 4;
    public const int FPDF_ERR_PASSWORD = 4;
    public const int FPDF_INCREMENTAL = 1;
    public const int FPDF_NO_INCREMENTAL = 2;
    public const int FPDF_REMOVE_SECURITY = 3;

    public const int FPDF_PAGEOBJ_TEXT = 1;
    public const int FPDF_PAGEOBJ_PATH = 2;
    public const int FPDF_PAGEOBJ_IMAGE = 3;
    public const int FPDF_PAGEOBJ_FORM = 5;

    public const int FPDF_ANNOT_TEXT = 1;
    public const int FPDF_ANNOT_SQUARE = 5;
    public const int FPDF_ANNOT_HIGHLIGHT = 9;
    public const int FPDF_ANNOT_INK = 15;
    public const int FPDF_ANNOT_POPUP = 16;
    public const int FPDF_ANNOT_LINK = 4;
    public const int FPDF_ANNOT_WIDGET = 20;

    public const int FPDF_FILLMODE_NONE = 0;
    public const int FPDF_FILLMODE_ALTERNATE = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_MATRIX { public float a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_RECTF { public float left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_POINTF { public float x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_QUADPOINTSF { public float x1, y1, x2, y2, x3, y3, x4, y4; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int WriteBlockDelegate(IntPtr pThis, IntPtr data, uint size);

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEWRITE
    {
        public int version;
        public IntPtr WriteBlock;
    }

    // ---------------------------------------------------------------- libreria e documenti
    [DllImport(Dll)] public static extern void FPDF_InitLibrary();
    [DllImport(Dll)] public static extern uint FPDF_GetLastError();
    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Dll)] public static extern IntPtr FPDF_CreateNewDocument();
    [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Dll)] public static extern int FPDF_SaveAsCopy(IntPtr doc, ref FPDF_FILEWRITE writer, uint flags);
    [DllImport(Dll)] public static extern int FPDF_ImportPagesByIndex(IntPtr dest, IntPtr src, int* indices, uint length, int index);
    [DllImport(Dll)] public static extern int FPDF_ImportPages(IntPtr dest, IntPtr src, [MarshalAs(UnmanagedType.LPStr)] string? range, int index);
    [DllImport(Dll)] public static extern int FPDF_MovePages(IntPtr doc, int* indices, uint length, int destIndex);

    // ---------------------------------------------------------------- moduli (campi compilabili)
    [DllImport(Dll)] public static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr doc, IntPtr formInfo);
    [DllImport(Dll)] public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr form);
    [DllImport(Dll)] public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);
    [DllImport(Dll)] public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);
    [DllImport(Dll)] public static extern void FPDF_FFLDraw(IntPtr form, IntPtr bitmap, IntPtr page, int x, int y, int w, int h, int rotate, int flags);

    // ---------------------------------------------------------------- pagine
    [DllImport(Dll)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Dll)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Dll)] public static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Dll)] public static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Dll)] public static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport(Dll)] public static extern void FPDFPage_SetRotation(IntPtr page, int rotate);
    [DllImport(Dll)] public static extern IntPtr FPDFPage_New(IntPtr doc, int index, double width, double height);
    [DllImport(Dll)] public static extern void FPDFPage_Delete(IntPtr doc, int index);
    [DllImport(Dll)] public static extern int FPDFPage_GenerateContent(IntPtr page);

    // ---------------------------------------------------------------- rendering
    [DllImport(Dll)] public static extern IntPtr FPDFBitmap_CreateEx(int w, int h, int format, IntPtr buffer, int stride);
    [DllImport(Dll)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int w, int h, uint color);
    [DllImport(Dll)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Dll)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int w, int h, int rotate, int flags);
    [DllImport(Dll)] public static extern int FPDF_DeviceToPage(IntPtr page, int x, int y, int w, int h, int rotate, int dx, int dy, out double px, out double py);
    [DllImport(Dll)] public static extern int FPDF_PageToDevice(IntPtr page, int x, int y, int w, int h, int rotate, double px, double py, out int dx, out int dy);

    // ---------------------------------------------------------------- testo
    [DllImport(Dll)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Dll)] public static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Dll)] public static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Dll)] public static extern int FPDFText_GetText(IntPtr textPage, int start, int count, char* buffer);
    [DllImport(Dll)] public static extern int FPDFText_CountRects(IntPtr textPage, int start, int count);
    [DllImport(Dll)] public static extern int FPDFText_GetRect(IntPtr textPage, int index, out double left, out double top, out double right, out double bottom);
    [DllImport(Dll)] public static extern IntPtr FPDFText_FindStart(IntPtr textPage, [MarshalAs(UnmanagedType.LPWStr)] string what, uint flags, int start);
    [DllImport(Dll)] public static extern int FPDFText_FindNext(IntPtr handle);
    [DllImport(Dll)] public static extern int FPDFText_GetSchResultIndex(IntPtr handle);
    [DllImport(Dll)] public static extern int FPDFText_GetSchCount(IntPtr handle);
    [DllImport(Dll)] public static extern void FPDFText_FindClose(IntPtr handle);
    [DllImport(Dll)] public static extern int FPDFText_GetBoundedText(IntPtr textPage, double left, double top, double right, double bottom, char* buffer, int length);

    // ---------------------------------------------------------------- oggetti di pagina
    [DllImport(Dll)] public static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Dll)] public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [DllImport(Dll)] public static extern int FPDFPage_RemoveObject(IntPtr page, IntPtr obj);
    [DllImport(Dll)] public static extern void FPDFPage_InsertObject(IntPtr page, IntPtr obj);
    [DllImport(Dll)] public static extern void FPDFPageObj_Destroy(IntPtr obj);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetType(IntPtr obj);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetBounds(IntPtr obj, out float left, out float bottom, out float right, out float top);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetMatrix(IntPtr obj, out FS_MATRIX matrix);
    [DllImport(Dll)] public static extern int FPDFPageObj_SetMatrix(IntPtr obj, ref FS_MATRIX matrix);
    [DllImport(Dll)] public static extern void FPDFPageObj_Transform(IntPtr obj, double a, double b, double c, double d, double e, double f);
    [DllImport(Dll)] public static extern int FPDFPageObj_SetFillColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetFillColor(IntPtr obj, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Dll)] public static extern IntPtr FPDFPageObj_NewTextObj(IntPtr doc, [MarshalAs(UnmanagedType.LPStr)] string font, float size);
    [DllImport(Dll)] public static extern int FPDFText_SetText(IntPtr textObj, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport(Dll)] public static extern int FPDFTextObj_SetTextRenderMode(IntPtr textObj, int mode);
    [DllImport(Dll)] public static extern uint FPDFTextObj_GetText(IntPtr textObj, IntPtr textPage, char* buffer, uint length);
    [DllImport(Dll)] public static extern int FPDFTextObj_GetFontSize(IntPtr textObj, out float size);
    [DllImport(Dll)] public static extern IntPtr FPDFTextObj_GetFont(IntPtr textObj);
    [DllImport(Dll)] public static extern nuint FPDFFont_GetBaseFontName(IntPtr font, byte* buffer, nuint length);
    [DllImport(Dll)] public static extern int FPDFFont_GetWeight(IntPtr font);
    [DllImport(Dll)] public static extern int FPDFFont_GetFlags(IntPtr font);
    [DllImport(Dll)] public static extern IntPtr FPDFPageObj_CreateNewRect(float x, float y, float w, float h);
    [DllImport(Dll)] public static extern int FPDFPath_SetDrawMode(IntPtr path, int fillMode, int stroke);
    [DllImport(Dll)] public static extern IntPtr FPDFPageObj_NewImageObj(IntPtr doc);
    [DllImport(Dll)] public static extern int FPDFImageObj_SetBitmap(IntPtr* pages, int count, IntPtr imageObj, IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFImageObj_SetMatrix(IntPtr imageObj, double a, double b, double c, double d, double e, double f);

    // ---------------------------------------------------------------- annotazioni
    [DllImport(Dll)] public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Dll)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Dll)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Dll)] public static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [DllImport(Dll)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Dll)] public static extern int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);
    [DllImport(Dll)] public static extern int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, ref FS_QUADPOINTSF quad);
    [DllImport(Dll)] public static extern int FPDFAnnot_SetRect(IntPtr annot, ref FS_RECTF rect);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetRect(IntPtr annot, out FS_RECTF rect);
    [DllImport(Dll)] public static extern int FPDFAnnot_AddInkStroke(IntPtr annot, FS_POINTF* points, nuint count);
    [DllImport(Dll)] public static extern int FPDFAnnot_SetBorder(IntPtr annot, float hRadius, float vRadius, float width);
    [DllImport(Dll)] public static extern int FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);

    private static int _initialized;

    public static void EnsureInit()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0) FPDF_InitLibrary();
    }
}
