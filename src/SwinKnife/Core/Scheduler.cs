using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SwinKnife.Core;

/// <summary>Crea, elenca ed elimina attività pianificate di Windows (schtasks) per l'utente corrente.</summary>
public static class Scheduler
{
    public const string Folder = "SwinKnife";

    public enum When { Daily, Weekly, AtLogon, Once }

    public sealed record Job(string Name, string Program, string Args, When When, string Time, string Day);

    public static string FullName(string name) => $"\\{Folder}\\{name}";

    // --------------------------------------------------- elenco salvato (per mostrare cosa ha creato l'app)
    public static List<Job> Saved()
    {
        try { return JsonSerializer.Deserialize<List<Job>>(Settings.Get("scheduler.jobs") ?? "[]") ?? new(); }
        catch { return new(); }
    }

    private static void Store(List<Job> jobs) => Settings.Set("scheduler.jobs", JsonSerializer.Serialize(jobs));

    public static void Create(Job job)
    {
        if (string.IsNullOrWhiteSpace(job.Name)) throw new InvalidOperationException(L.T("Scrivi un nome per l'attività."));
        if (string.IsNullOrWhiteSpace(job.Program) || !File.Exists(job.Program))
            throw new InvalidOperationException(L.T("Scegli un programma o uno script esistente."));

        var tr = string.IsNullOrWhiteSpace(job.Args) ? $"\"{job.Program}\"" : $"\"{job.Program}\" {job.Args}";
        var args = new List<string> { "/Create", "/TN", FullName(job.Name), "/TR", tr, "/F", "/RL", "LIMITED" };
        switch (job.When)
        {
            case When.Daily: args.AddRange(["/SC", "DAILY", "/ST", job.Time]); break;
            case When.Weekly: args.AddRange(["/SC", "WEEKLY", "/D", job.Day, "/ST", job.Time]); break;
            case When.AtLogon: args.AddRange(["/SC", "ONLOGON"]); break;
            case When.Once: args.AddRange(["/SC", "ONCE", "/SD", DateTime.Now.ToString("dd/MM/yyyy"), "/ST", job.Time]); break;
        }
        RunSchtasks(args);

        var jobs = Saved();
        jobs.RemoveAll(j => j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase));
        jobs.Add(job);
        Store(jobs);
    }

    public static void RunNow(string name) => RunSchtasks(["/Run", "/TN", FullName(name)]);

    public static void Delete(string name)
    {
        try { RunSchtasks(["/Delete", "/TN", FullName(name), "/F"]); } catch { /* già rimossa a mano */ }
        var jobs = Saved();
        jobs.RemoveAll(j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Store(jobs);
    }

    public static bool Exists(string name)
    {
        try { return RunSchtasksRaw(["/Query", "/TN", FullName(name)]).code == 0; }
        catch { return false; }
    }

    private static void RunSchtasks(IEnumerable<string> args)
    {
        var (code, output) = RunSchtasksRaw(args);
        if (code != 0)
            throw new InvalidOperationException(output.Trim().Length > 0
                ? output.Trim()
                : L.T($"schtasks ha restituito il codice {code}."));
    }

    private static (int code, string output) RunSchtasksRaw(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, string.IsNullOrWhiteSpace(err) ? outp : err);
    }
}
