using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace SwinKnife.Core;

public enum SignStatus { Unsigned, Valid, Invalid }

/// <summary>Controlla la firma digitale (Authenticode incorporata) di un file e ne ricava il firmatario.</summary>
public static class FileSignature
{
    public static (SignStatus status, string signer) Check(string path)
    {
        var signer = "";
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            signer = cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch { /* nessuna firma incorporata leggibile */ }

        var code = Verify(path);
        var status = code switch
        {
            0 => SignStatus.Valid,
            // nessuna firma presente / tipo di file non firmabile: non è "alterato", è semplicemente non firmato
            unchecked((int)0x800B0100) => SignStatus.Unsigned, // TRUST_E_NOSIGNATURE
            unchecked((int)0x800B0003) => SignStatus.Unsigned, // TRUST_E_SUBJECT_FORM_UNKNOWN (es. .txt, .jpg)
            unchecked((int)0x800B0001) => SignStatus.Unsigned, // TRUST_E_PROVIDER_UNKNOWN
            // firma presente ma non verificata (alterata, scaduta, non attendibile): davvero sospetta
            _ => SignStatus.Invalid,
        };
        return (status, signer);
    }

    private static int Verify(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, pFile, false);
        var data = new WINTRUST_DATA
        {
            cbStruct = Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = 2,            // WTD_UI_NONE
            fdwRevocationChecks = 0,   // WTD_REVOKE_NONE (nessuna rete)
            dwUnionChoice = 1,         // WTD_CHOICE_FILE
            dwStateAction = 1,         // WTD_STATEACTION_VERIFY
            pFile = pFile,
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        try
        {
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        catch { return unchecked((int)0x80004005); }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public int cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public int dwUIChoice;
        public int fdwRevocationChecks;
        public int dwUnionChoice;
        public IntPtr pFile;
        public int dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public int dwProvFlags;
        public int dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
}
