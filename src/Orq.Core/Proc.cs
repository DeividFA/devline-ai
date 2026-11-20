using System.Diagnostics;
using System.Text;

namespace Orq.Core;

public sealed record ProcResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
    public string All => (Stdout + "\n" + Stderr).Trim();
}

public static class Proc
{
    /// <summary>Localiza um executável no PATH (.exe, .cmd, .bat), sem depender do shell.</summary>
    public static string? Find(string name)
    {
        var exts = new[] { ".exe", ".cmd", ".bat", "" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in dirs)
            foreach (var ext in exts)
            {
                try
                {
                    var full = Path.Combine(dir.Trim('"'), name + ext);
                    if (File.Exists(full)) return full;
                }
                catch (ArgumentException) { }
            }
        return null;
    }

    public static Task<ProcResult> RunAsync(string file, IEnumerable<string> args, string? cwd, string? stdin,
        TimeSpan timeout, CancellationToken ct)
    {
        var psi = Base(file, cwd);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return RunAsync(psi, stdin, timeout, ct);
    }

    /// <summary>Executa uma linha de comando definida pelo usuário na configuração (ex.: comando de testes).</summary>
    public static Task<ProcResult> RunShellAsync(string commandLine, string cwd, TimeSpan timeout, CancellationToken ct)
    {
        var psi = Base("cmd.exe", cwd);
        psi.Arguments = "/d /c " + commandLine;
        return RunAsync(psi, null, timeout, ct);
    }

    private static ProcessStartInfo Base(string file, string? cwd) => new(file)
    {
        WorkingDirectory = cwd ?? "",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        StandardInputEncoding = new UTF8Encoding(false),
    };

    private static async Task<ProcResult> RunAsync(ProcessStartInfo psi, string? stdin, TimeSpan timeout, CancellationToken ct)
    {
        using var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new ProcResult(-1, "", "não foi possível iniciar o processo: " + ex.Message, false);
        }

        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        try
        {
            if (stdin != null) await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }
        catch (IOException) { }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch { }
            if (!timedOut) throw;
        }

        string o = "", e = "";
        try { o = await so.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        try { e = await se.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        return new ProcResult(timedOut ? -1 : p.ExitCode, o, e, timedOut);
    }
}

/// <summary>Trava entre processos (frentes) por arquivo exclusivo: protege worktree add e integração.</summary>
public static class FileLock
{
    public static async Task<IDisposable> AcquireAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(250, ct);
        }
    }
}

/// <summary>Git restrito: toda chamada passa pelo Guard antes de executar.</summary>
public sealed class Git
{
    public async Task<ProcResult> Run(string cwd, params string[] args)
    {
        Guard.CheckGit(args);
        var full = new List<string> { "-c", "user.name=Orquestrador", "-c", "user.email=orq@localhost", "-c", "core.quotepath=false" };
        full.AddRange(args);
        return await Proc.RunAsync("git", full, cwd, null, TimeSpan.FromMinutes(2), CancellationToken.None);
    }

    public async Task<string> Ok(string cwd, params string[] args)
    {
        var r = await Run(cwd, args);
        if (!r.Ok) throw new InvalidOperationException($"git {string.Join(' ', args)} falhou: {Redactor.Tail(r.All, 600)}");
        return r.Stdout.Trim();
    }
}
