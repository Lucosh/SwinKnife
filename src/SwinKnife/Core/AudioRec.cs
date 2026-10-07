using System.Runtime.InteropServices;
using System.Text;

namespace SwinKnife.Core;

/// <summary>Registrazione audio dal microfono con l'API MCI di Windows (winmm): nessuna libreria esterna.</summary>
public sealed class AudioRec : IDisposable
{
    private const string Alias = "swkrec";
    private bool _open;

    public void Start()
    {
        Close();
        Mci("open new type waveaudio alias " + Alias);
        _open = true;
        try
        {
            Mci($"set {Alias} bitspersample 16 channels 2 samplespersec 44100 bytespersec 176400 alignment 4");
            Mci("record " + Alias);
        }
        catch
        {
            Close();
            throw;
        }
    }

    /// <summary>Ferma la registrazione e salva in un file WAV.</summary>
    public void Stop(string wavPath)
    {
        if (!_open) throw new InvalidOperationException(L.T("Nessuna registrazione in corso."));
        Mci("stop " + Alias);
        Mci($"save {Alias} \"{wavPath}\"");
        Close();
    }

    private void Close()
    {
        if (!_open) return;
        try { mciSendString("close " + Alias, null, 0, IntPtr.Zero); } catch { }
        _open = false;
    }

    private static void Mci(string command)
    {
        var code = mciSendString(command, null, 0, IntPtr.Zero);
        if (code != 0)
        {
            var sb = new StringBuilder(256);
            mciGetErrorString(code, sb, sb.Capacity);
            throw new InvalidOperationException(sb.Length > 0 ? sb.ToString() : L.T("Errore del registratore audio (codice ") + code + ").");
        }
    }

    public void Dispose() => Close();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? ret, int retLen, IntPtr hwndCallback);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool mciGetErrorString(int error, StringBuilder buffer, int bufferLen);
}
