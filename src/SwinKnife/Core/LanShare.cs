using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SwinKnife.Core;

/// <summary>
/// Scambio di file col telefono sulla rete locale: un piccolo server web (senza installare nulla sul telefono)
/// raggiungibile dal QR code. L'indirizzo contiene un codice casuale, quindi solo chi lo inquadra può entrare.
/// </summary>
public sealed class LanShare : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    public List<string> Shared { get; } = new();
    public string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));
    public int Port { get; private set; }
    public string? Address { get; private set; }
    public string Url => $"http://{Address}:{Port}/{Token}/";
    public bool Running => _listener != null;
    /// <summary>Solo per le prove: ascolta su un indirizzo diverso (es. 127.0.0.1, senza avviso del firewall).</summary>
    internal IPAddress Bind { get; set; } = IPAddress.Any;

    public static string ReceiveDir
    {
        get => Settings.Get("phone.dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SwinKnife");
        set => Settings.Set("phone.dir", value);
    }

    public event Action<string>? Received;      // percorso del file arrivato
    public event Action<string>? Activity;      // messaggio per il registro

    /// <summary>Indirizzo IPv4 della rete locale (preferendo la scheda con gateway: Wi-Fi o cavo).</summary>
    public static string? LocalAddress()
    {
        var best = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Select(n => (n, p: n.GetIPProperties()))
            .SelectMany(x => x.p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => (a.Address, gw: x.p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)),
                    virt: x.n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || x.n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                          || x.n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase) || x.n.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase))))
            .Where(x => IsPrivate(x.Address))
            .OrderByDescending(x => x.gw).ThenBy(x => x.virt)
            .FirstOrDefault();
        return best.Address?.ToString();
    }

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public void Start()
    {
        if (Running) return;
        Address = Bind.Equals(IPAddress.Any) ? LocalAddress() ?? throw new InvalidOperationException(L.T("Il PC non è collegato a una rete locale (Wi-Fi o cavo).")) : Bind.ToString();
        foreach (var port in new[] { 8765, 8766, 8767, 0 })
        {
            try
            {
                _listener = new TcpListener(Bind, port);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                break;
            }
            catch (SocketException)
            {
                _listener = null;
            }
        }
        if (_listener == null) throw new InvalidOperationException(L.T("Nessuna porta di rete disponibile."));
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(_listener, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => Handle(client, ct), ct);
        }
    }

    // ------------------------------------------------------------------ HTTP minimo
    private async Task Handle(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                var (method, target, headers, rest) = await ReadHead(stream, ct);
                if (method == null) return;
                var who = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
                var path = target!.Split('?')[0];
                if (!path.StartsWith($"/{Token}/", StringComparison.Ordinal))
                {
                    await Send(stream, 403, "text/plain", Encoding.UTF8.GetBytes("Forbidden"));
                    return;
                }
                var sub = path[(Token.Length + 2)..];
                if (method == "GET" && sub.Length == 0)
                    await Send(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page()));
                else if (method == "GET" && sub.StartsWith("f/") && int.TryParse(sub[2..], out var i))
                    await SendFile(stream, i, who, ct);
                else if (method == "POST" && sub == "upload")
                    await Receive(stream, headers, rest, who, ct);
                else
                    await Send(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // il telefono ha chiuso la connessione
            }
            catch (Exception ex)
            {
                AppInfo.Log(ex, "Invia al telefono");
            }
        }
    }

    private static async Task<(string? method, string? target, Dictionary<string, string> headers, byte[] rest)> ReadHead(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[16384];
        var len = 0;
        while (len < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(len), ct);
            if (n == 0) return (null, null, new(), []);
            len += n;
            var end = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;
            var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return (null, null, new(), []);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1))
            {
                var c = l.IndexOf(':');
                if (c > 0) headers[l[..c].Trim()] = l[(c + 1)..].Trim();
            }
            return (first[0], first[1], headers, buf[(end + 4)..len]);
        }
        return (null, null, new(), []);
    }

    private static async Task Send(NetworkStream s, int code, string type, byte[] body, string? extra = null)
    {
        var status = code switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var head = $"HTTP/1.1 {code} {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n{extra}\r\n";
        await s.WriteAsync(Encoding.UTF8.GetBytes(head));
        await s.WriteAsync(body);
    }

    private async Task SendFile(NetworkStream s, int i, string who, CancellationToken ct)
    {
        string? path;
        lock (_lock) path = i >= 0 && i < Shared.Count ? Shared[i] : null;
        if (path == null || !File.Exists(path))
        {
            await Send(s, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
            return;
        }
        var name = Path.GetFileName(path);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, true);
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {fs.Length}\r\n" +
                   $"Content-Disposition: attachment; filename=\"{Ascii(name)}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.UTF8.GetBytes(head), ct);
        await fs.CopyToAsync(s, 1 << 16, ct);
        Activity?.Invoke(L.T($"{who} ha scaricato {name}"));
    }

    private static string Ascii(string s) => new(s.Select(c => c < 128 && c != '"' ? c : '_').ToArray());

    /// <summary>Il telefono invia un file alla volta con il corpo grezzo e il nome nell'intestazione X-File-Name.</summary>
    private async Task Receive(NetworkStream s, Dictionary<string, string> headers, byte[] rest, string who, CancellationToken ct)
    {
        if (!headers.TryGetValue("Content-Length", out var cl) || !long.TryParse(cl, out var length)) return;
        var name = Util.SafeFileName(Uri.UnescapeDataString(headers.GetValueOrDefault("X-File-Name", "file")));
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        Directory.CreateDirectory(ReceiveDir);
        var dest = Util.UniquePath(Path.Combine(ReceiveDir, name));
        var tmp = dest + ".part";
        try
        {
            await using (var fs = File.Create(tmp))
            {
                await fs.WriteAsync(rest, ct);
                var remaining = length - rest.Length;
                var buf = new byte[1 << 16];
                while (remaining > 0)
                {
                    var n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), ct);
                    if (n == 0) throw new IOException("connessione interrotta");
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    remaining -= n;
                }
            }
            File.Move(tmp, dest);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        await Send(s, 200, "application/json", "{\"ok\":true}"u8.ToArray());
        Activity?.Invoke(L.T($"Ricevuto da {who}: {Path.GetFileName(dest)}"));
        Received?.Invoke(dest);
    }

    // ------------------------------------------------------------------ pagina per il telefono
    private string Page()
    {
        List<string> files;
        lock (_lock) files = Shared.ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < files.Count; i++)
        {
            var f = new FileInfo(files[i]);
            if (!f.Exists) continue;
            sb.Append($"<a class=\"file\" href=\"f/{i}\" download><span class=\"n\">{WebUtility.HtmlEncode(f.Name)}</span><span class=\"s\">{Util.HumanSize(f.Length)}</span></a>");
        }
        var list = sb.Length > 0 ? sb.ToString() : $"<p class=\"empty\">{WebUtility.HtmlEncode(L.T("Nessun file condiviso dal PC per ora."))}</p>";
        string T(string s) => WebUtility.HtmlEncode(s);
        return $$"""
<!doctype html><html lang="{{L.Code}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>SwinKnife</title><style>
body{margin:0;font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:#1c1c1f;color:#eee}
header{background:#e5484d;color:#fff;padding:18px 20px;font-size:20px;font-weight:600}
main{padding:16px;max-width:640px;margin:auto}h2{font-size:15px;color:#bbb;margin:22px 0 10px;text-transform:uppercase;letter-spacing:.04em}
.file{display:flex;justify-content:space-between;gap:12px;background:#2a2a2e;border-radius:10px;padding:14px;margin-bottom:8px;color:#fff;text-decoration:none}
.n{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.s{color:#999;flex:none}.empty{color:#999}
label.btn{display:block;text-align:center;background:#e5484d;color:#fff;border-radius:10px;padding:16px;font-size:17px;font-weight:600}
input{display:none}#log div{background:#2a2a2e;border-radius:8px;padding:10px 12px;margin-top:8px;font-size:14px}
progress{width:100%;height:8px;margin-top:6px;accent-color:#e5484d}
</style></head><body><header>SwinKnife</header><main>
<h2>{{T(L.T("Invia al PC"))}}</h2>
<label class="btn">{{T(L.T("Scegli foto o file"))}}<input id="pick" type="file" multiple></label>
<div id="log"></div>
<h2>{{T(L.T("Scarica dal PC"))}}</h2>{{list}}
</main><script>
const pick=document.getElementById('pick'),log=document.getElementById('log');
pick.onchange=async()=>{for(const f of pick.files){const row=document.createElement('div');row.textContent=f.name;const bar=document.createElement('progress');bar.max=100;bar.value=0;row.appendChild(bar);log.prepend(row);
await new Promise(res=>{const x=new XMLHttpRequest();x.open('POST','upload');x.setRequestHeader('X-File-Name',encodeURIComponent(f.name));
x.upload.onprogress=e=>{if(e.lengthComputable)bar.value=e.loaded*100/e.total};
x.onload=()=>{bar.value=100;row.firstChild.textContent='✓ '+f.name;res()};x.onerror=()=>{row.firstChild.textContent='✗ '+f.name;res()};x.send(f)});}pick.value='';};
</script></body></html>
""";
    }

    public void Add(IEnumerable<string> paths)
    {
        lock (_lock)
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) Shared.AddRange(Directory.GetFiles(p).Where(f => !Shared.Contains(f)));
                else if (File.Exists(p) && !Shared.Contains(p)) Shared.Add(p);
            }
    }

    public void Remove(string path)
    {
        lock (_lock) Shared.Remove(path);
    }

    /// <summary>Regola del firewall di Windows per accettare connessioni dalla rete privata (chiede l'amministratore).</summary>
    public static bool AllowInFirewall()
    {
        var exe = Environment.ProcessPath!;
        var args = $"/c netsh advfirewall firewall delete rule name=\"SwinKnife\" >nul & netsh advfirewall firewall add rule name=\"SwinKnife\" dir=in action=allow program=\"{exe}\" enable=yes profile=private,domain";
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            p?.WaitForExit(15000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
