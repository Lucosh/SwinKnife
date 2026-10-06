using System.IO.Pipes;
using System.Text.Json;

namespace SwinKnife.Core;

/// <summary>
/// Una sola finestra di SwinKnife: le aperture successive (es. dal menu del tasto destro)
/// inviano i propri argomenti all'istanza già avviata tramite una named pipe.
/// </summary>
public static class SingleInstance
{
    private static readonly string Id = "SwinKnife." + Environment.UserName;
    private static Mutex? _mutex;

    /// <summary>True se questa è la prima istanza. Con wait=true attende che l'istanza precedente si chiuda.</summary>
    public static bool Acquire(bool wait)
    {
        _mutex = new Mutex(false, @"Local\" + Id);
        try
        {
            return _mutex.WaitOne(wait ? 15000 : 0);
        }
        catch (AbandonedMutexException)
        {
            return true; // l'istanza precedente è terminata senza rilasciarlo
        }
    }

    public static bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(3000);
            using var w = new StreamWriter(client);
            w.Write(JsonSerializer.Serialize(args.Select(a => File.Exists(a) || Directory.Exists(a) ? Path.GetFullPath(a) : a).ToArray()));
            w.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Listen(Action<string[]> onArgs)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var r = new StreamReader(server);
                    var args = JsonSerializer.Deserialize<string[]>(r.ReadToEnd()) ?? [];
                    onArgs(args);
                }
                catch (Exception ex)
                {
                    AppInfo.Log(ex, "Pipe istanza singola");
                    Thread.Sleep(500);
                }
            }
        }) { IsBackground = true, Name = "SwinKnife pipe" };
        t.Start();
    }
}
