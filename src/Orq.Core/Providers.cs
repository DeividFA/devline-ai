using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orq.Core;

/// <summary>Classifica mensagens de erro dos provedores. Só é usado quando a execução já falhou.</summary>
public static partial class Classifier
{
    [GeneratedRegex(@"(?i)usage limit|rate.?limit|hit your limit|limit reached|too many requests|\b429\b|quota|limite de uso|try again at|resets? (at|in)\b")]
    private static partial Regex RateRx();

    [GeneratedRegex(@"(?i)not logged in|unauthori[sz]ed|\b401\b|\b403\b|authentication|invalid api key|/login|login required|credentials|sign in")]
    private static partial Regex AuthRx();

    [GeneratedRegex(@"(?i)ECONNREFUSED|ENOTFOUND|ETIMEDOUT|ECONNRESET|EHOSTUNREACH|ENETUNREACH|network|reconnecting|connection (failed|failure|refused|reset|error|timed?\s*out|timeout)|connect(?:ion)?[-\s]*timeout|timed?\s*out (?:while )?connect(?:ing)?|error sending request|stream disconnected")]
    private static partial Regex NetworkRx();

    [GeneratedRegex(@"(?i)\b50[0234]\b|\b529\b|overloaded|service unavailable|não foi possível iniciar")]
    private static partial Regex UnavailableRx();

    public static Outcome Classify(string? error)
    {
        var e = error ?? "";
        if (RateRx().IsMatch(e)) return Outcome.RateLimited;
        if (AuthRx().IsMatch(e)) return Outcome.AuthError;
        if (NetworkRx().IsMatch(e)) return Outcome.NetworkError;
        if (UnavailableRx().IsMatch(e)) return Outcome.Unavailable;
        return Outcome.Failed;
    }

    public static Outcome ProcessFailure(ProcResult pr, string? detail = null)
    {
        var outcome = Classify(detail + "\n" + pr.All);
        return outcome == Outcome.Failed && pr.TimedOut ? Outcome.Timeout : outcome;
    }
}

/// <summary>Claude Code via CLI oficial em modo não interativo (-p --output-format json).</summary>
public sealed class ClaudeProvider(ProviderConfig cfg) : IProvider
{
    public string Name => "claude";
    public bool CanEditFiles => true;

    public static List<string> BuildArgs(AgentRequest req, string? model)
    {
        // Sem Bash/PowerShell: o agente só lê e edita arquivos; quem roda testes e git é o orquestrador.
        var args = new List<string>
        {
            "-p", "--output-format", "json",
            "--permission-prompts", "none",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--tools", req.ReadOnly ? "Read,Grep,Glob" : "Read,Grep,Glob,Edit,Write",
        };
        if (!req.ReadOnly) { args.Add("--permission-mode"); args.Add("acceptEdits"); }
        if (!string.IsNullOrWhiteSpace(model)) { args.Add("--model"); args.Add(model); }
        if (!string.IsNullOrWhiteSpace(req.ResumeSessionId)) { args.Add("--resume"); args.Add(req.ResumeSessionId); }
        foreach (var dir in req.ReadDirs.Where(Directory.Exists)) { args.Add("--add-dir"); args.Add(dir); }
        return args;
    }

    public async Task<ProviderResult> RunAsync(AgentRequest req, CancellationToken ct)
    {
        var exe = Proc.Find("claude");
        if (exe == null) return new ProviderResult { Outcome = Outcome.Unavailable, Error = "claude não encontrado no PATH" };
        var pr = await Proc.RunAsync(exe, BuildArgs(req, req.Model ?? cfg.Model), req.Cwd, req.Prompt, req.Timeout, ct);
        return Parse(pr);
    }

    public static ProviderResult Parse(ProcResult pr)
    {
        JsonElement? root = null;
        try
        {
            using var doc = JsonDocument.Parse(pr.Stdout);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Array)
                foreach (var el in r.EnumerateArray())
                    if (el.Str("type") == "result") root = el.Clone();
            if (r.ValueKind == JsonValueKind.Object) root = r.Clone();
        }
        catch (JsonException) { }

        if (root is not { } j)
        {
            var err = pr.All;
            return new ProviderResult { Outcome = pr.Ok ? Outcome.Failed : Classifier.ProcessFailure(pr), Error = Redactor.Tail(err, 800), Text = pr.Stdout };
        }

        var res = new ProviderResult { Text = j.Str("result") ?? "", SessionId = j.Str("session_id") };
        if (j.TryGetProperty("usage", out var u))
        {
            res.Usage.Input = u.Num("input_tokens") + u.Num("cache_creation_input_tokens") + u.Num("cache_read_input_tokens");
            res.Usage.Output = u.Num("output_tokens");
            res.Usage.Kind = UsageKind.Known;
        }
        if (j.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) res.Usage.CostUsd = c.GetDouble();

        var isError = j.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
        if (isError || !pr.Ok || pr.TimedOut)
        {
            res.Error = Redactor.Tail(res.Text.Length > 0 ? res.Text : pr.All, 800);
            res.Outcome = Classifier.ProcessFailure(pr, res.Text + " " + (j.Str("subtype") ?? "") + " " + j.Num("api_error_status"));
        }
        else res.Outcome = Outcome.Success;
        return res;
    }
}

/// <summary>OpenAI Codex CLI via `codex exec --json` (eventos JSONL), com sandbox do próprio Codex.</summary>
public sealed class CodexProvider(ProviderConfig cfg) : IProvider
{
    public string Name => "codex";
    public bool CanEditFiles => true;

    public static List<string> BuildArgs(AgentRequest req, string? model)
    {
        var sandbox = req.ReadOnly ? "read-only" : "workspace-write";
        var args = new List<string> { "exec" };
        if (!string.IsNullOrWhiteSpace(req.ResumeSessionId))
        {
            args.AddRange(new[] { "resume", req.ResumeSessionId!, "--json", "--skip-git-repo-check", "-c", $"sandbox_mode={sandbox}" });
        }
        else
        {
            args.AddRange(new[] { "--json", "--skip-git-repo-check", "-s", sandbox, "-C", req.Cwd });
        }
        if (!string.IsNullOrWhiteSpace(model))
        {
            var partes = model.Split('@', 2);
            args.Add("-m"); args.Add(partes[0]);
            if (partes.Length == 2 && partes[1].Length > 0) { args.Add("-c"); args.Add("model_reasoning_effort=" + partes[1]); }
        }
        args.Add("-"); // prompt pela entrada padrão
        return args;
    }

    public async Task<ProviderResult> RunAsync(AgentRequest req, CancellationToken ct)
    {
        var exe = Proc.Find("codex");
        if (exe == null) return new ProviderResult { Outcome = Outcome.Unavailable, Error = "codex não encontrado no PATH" };
        var pr = await Proc.RunAsync(exe, BuildArgs(req, req.Model ?? cfg.Model), req.Cwd, req.Prompt, req.Timeout, ct);
        return Parse(pr);
    }

    public static ProviderResult Parse(ProcResult pr)
    {
        var res = new ProviderResult();
        string? error = null;
        var completed = false;
        foreach (var line in pr.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var l = line.Trim();
            if (!l.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(l);
                var e = doc.RootElement;
                switch (e.Str("type"))
                {
                    case "thread.started":
                        res.SessionId = e.Str("thread_id") ?? res.SessionId;
                        break;
                    case "item.completed":
                        if (e.TryGetProperty("item", out var item) && item.Str("type") == "agent_message")
                            res.Text = item.Str("text") ?? res.Text;
                        break;
                    case "turn.completed":
                        completed = true;
                        if (e.TryGetProperty("usage", out var u))
                        {
                            res.Usage.Input += u.Num("input_tokens");
                            res.Usage.Output += u.Num("output_tokens");
                            res.Usage.Kind = UsageKind.Known;
                        }
                        break;
                    case "turn.failed":
                        error = e.TryGetProperty("error", out var err) ? err.Str("message") ?? err.ToString() : "turn.failed";
                        break;
                    case "error":
                        error = e.Str("message") ?? l;
                        break;
                }
            }
            catch (JsonException) { }
        }

        if (completed && pr.Ok && error == null) { res.Outcome = Outcome.Success; return res; }
        res.Error = Redactor.Tail(error ?? pr.All, 800);
        res.Outcome = Classifier.ProcessFailure(pr, error);
        return res;
    }
}

/// <summary>Ollama pela API HTTP local. Só devolve texto: a inferência não sai do computador.</summary>
public sealed class OllamaProvider(ProviderConfig cfg, HttpMessageHandler? handler = null) : IProvider
{
    public string Name => "ollama";
    public bool CanEditFiles => false;

    public async Task<ProviderResult> RunAsync(AgentRequest req, CancellationToken ct)
    {
        using var http = handler == null ? new HttpClient() : new HttpClient(handler, false);
        http.Timeout = req.Timeout;
        var url = (cfg.Url ?? "http://localhost:11434").TrimEnd('/') + "/api/chat";
        var body = new
        {
            model = req.Model ?? cfg.Model ?? "gemma3:4b",
            stream = false,
            messages = new[] { new { role = "user", content = req.Prompt } },
        };
        try
        {
            using var resp = await http.PostAsJsonAsync(url, body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var error = $"ollama HTTP {(int)resp.StatusCode}: {Redactor.Head(text, 300)}";
                var outcome = Classifier.Classify(error);
                return new ProviderResult { Outcome = outcome == Outcome.Failed ? Outcome.Unavailable : outcome, Error = error };
            }
            using var doc = JsonDocument.Parse(text);
            var j = doc.RootElement;
            var res = new ProviderResult { Outcome = Outcome.Success };
            if (j.TryGetProperty("message", out var m)) res.Text = m.Str("content") ?? "";
            res.Usage = new Usage { Input = j.Num("prompt_eval_count"), Output = j.Num("eval_count"), CostUsd = 0, Kind = UsageKind.Known };
            if (string.IsNullOrWhiteSpace(res.Text)) { res.Outcome = Outcome.Failed; res.Error = "resposta vazia"; }
            return res;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            var network = Classifier.Classify(ex.ToString()) == Outcome.NetworkError;
            return new ProviderResult { Outcome = network ? Outcome.NetworkError : Outcome.Timeout,
                Error = network ? ex.Message : "tempo limite excedido" };
        }
        catch (HttpRequestException ex)
        {
            return new ProviderResult { Outcome = Outcome.NetworkError, Error = "ollama indisponível: " + ex.Message };
        }
    }
}

/// <summary>Provedor simulado: permite testar paralelismo, falhas e limites sem gastar tokens.</summary>
public sealed class SimulatedProvider(string name) : IProvider
{
    public sealed record Call(AgentRequest Req, DateTime Start, DateTime End, ProviderResult Result);

    public string Name => name;
    public bool CanEditFiles { get; init; } = true;
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Roteiro opcional: (pedido, nº da chamada, ct) → resultado; null usa o comportamento padrão.</summary>
    public Func<AgentRequest, int, CancellationToken, Task<ProviderResult?>>? Script { get; set; }

    public ConcurrentQueue<Call> Calls { get; } = new();
    private int _n;

    public async Task<ProviderResult> RunAsync(AgentRequest req, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _n);
        var start = DateTime.UtcNow;
        await Task.Delay(Delay, ct);
        var r = (Script == null ? null : await Script(req, n, ct)) ?? Default(req);
        r.SessionId ??= $"sim-{name}-{req.TaskId}";
        if (r.Usage.Kind == UsageKind.Unknown && r.Outcome == Outcome.Success)
            r.Usage = new Usage { Input = req.Prompt.Length / 4, Output = r.Text.Length / 4, CostUsd = 0, Kind = UsageKind.Known };
        Calls.Enqueue(new Call(req, start, DateTime.UtcNow, r));
        return r;
    }

    public ProviderResult Default(AgentRequest req)
    {
        if (req.Role == Role.Reviewer)
            return new ProviderResult { Outcome = Outcome.Success, Text = "{\"verdict\":\"approve\",\"notes\":\"ok (simulado)\"}" };
        if (req.ReadOnly)
            return new ProviderResult { Outcome = Outcome.Success, Text = $"Análise simulada por {name} para {req.TaskId}.\n{{\"summary\":\"análise de {req.TaskId} por {name}\",\"decisions\":[],\"pending\":[]}}" };
        if (!CanEditFiles)
            return new ProviderResult { Outcome = Outcome.Success, Text = $"conteúdo gerado por {name} para {req.TaskId}\n" };
        WriteFiles(req, $"gerado por {name} para {req.TaskId}\n");
        return new ProviderResult { Outcome = Outcome.Success, Text = $"{{\"summary\":\"{req.TaskId} implementada por {name}\",\"decisions\":[\"simulado\"],\"pending\":[]}}" };
    }

    public static void WriteFiles(AgentRequest req, string content)
    {
        foreach (var f in req.Files.Where(Scope.IsLiteral))
            Write(req, f, content);
    }

    public static void Write(AgentRequest req, string relative, string content)
    {
        var full = Path.Combine(req.Cwd, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
