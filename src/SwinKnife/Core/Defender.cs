using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SwinKnife.Core;

public enum DefenderScan { Quick, Full, Custom }

/// <summary>Una minaccia rilevata da Windows Defender.</summary>
public sealed record DefenderThreat(string Name, string Resource, DateTime When);

public sealed record DefenderResult(bool Ok, IReadOnlyList<DefenderThreat> Threats, string Message);

/// <summary>Pilota Windows Defender (Microsoft Defender) tramite i suoi cmdlet PowerShell.</summary>
public static class Defender
{
    public static bool Available()
    {
        foreach (var p in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "Microsoft", "Windows Defender"),
        })
            if (File.Exists(p) || Directory.Exists(p)) return true;
        return OperatingSystem.IsWindows();
    }

    public static async Task<DefenderResult> ScanAsync(DefenderScan type, string? path, CancellationToken ct)
    {
        var started = DateTime.Now.AddSeconds(-2);
        var scanArgs = type switch
        {
            DefenderScan.Quick => "-ScanType QuickScan",
            DefenderScan.Full => "-ScanType FullScan",
            _ => $"-ScanType CustomScan -ScanPath '{(path ?? "").Replace("'", "''")}'",
        };
        var (code, _, err) = await RunPwsh($"Start-MpScan {scanArgs}", ct);
        if (code != 0 && err.Length > 0)
            return new DefenderResult(false, [], L.T("Windows Defender ha segnalato un problema:\n") + err.Trim());

        var threats = await RecentThreatsAsync(started, ct);
        var msg = threats.Count == 0
            ? L.T("Scansione completata: nessuna minaccia trovata da Windows Defender.")
            : L.T($"Scansione completata: Windows Defender ha rilevato {threats.Count} minacce (di solito le mette in quarantena da sé).");
        return new DefenderResult(true, threats, msg);
    }

    public static async Task<IReadOnlyList<DefenderThreat>> RecentThreatsAsync(DateTime since, CancellationToken ct)
    {
        const string script =
            "$t = Get-MpThreat; Get-MpThreatDetection | ForEach-Object { $tid=$_.ThreatID; " +
            "$n=($t | Where-Object ThreatID -eq $tid | Select-Object -First 1 -ExpandProperty ThreatName); " +
            "[pscustomobject]@{ Name=$n; Resources=$_.Resources; Time=$_.InitialDetectionTime } } | ConvertTo-Json -Depth 4";
        var (_, outp, _) = await RunPwsh(script, ct);
        var list = new List<DefenderThreat>();
        if (string.IsNullOrWhiteSpace(outp)) return list;
        try
        {
            using var doc = JsonDocument.Parse(outp.Trim());
            var root = doc.RootElement;
            foreach (var e in root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : new[] { root })
            {
                var name = e.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : L.T("minaccia sconosciuta");
                var when = e.TryGetProperty("Time", out var t) && t.ValueKind == JsonValueKind.String && DateTime.TryParse(t.GetString(), out var dt) ? dt : DateTime.MinValue;
                var res = "";
                if (e.TryGetProperty("Resources", out var r))
                    res = r.ValueKind == JsonValueKind.Array ? string.Join("; ", r.EnumerateArray().Select(x => Clean(x.GetString()))) : Clean(r.GetString());
                if (when == DateTime.MinValue || when >= since)
                    list.Add(new DefenderThreat(name, res, when));
            }
        }
        catch { /* niente o JSON non valido */ }
        return list;
    }

    private static string Clean(string? resource) =>
        (resource ?? "").Replace("file:_", "").Replace("containerfile:_", "");

    private static async Task<(int code, string output, string error)> RunPwsh(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await outTask, await errTask);
    }
}
