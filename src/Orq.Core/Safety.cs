using System.Text.RegularExpressions;

namespace Orq.Core;

/// <summary>Remove segredos de qualquer texto antes de gravar em log, banco ou handoff.</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_\w{20,}|eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}")]
    private static partial Regex TokenRx();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._\-]{16,}")]
    private static partial Regex BearerRx();

    [GeneratedRegex(@"(?i)\b(password|passwd|senha|secret|token|api[_-]?key|access[_-]?key)(""?\s*[:=]\s*""?)([^\s""',;]+)")]
    private static partial Regex KeyValueRx();

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = TokenRx().Replace(text, "[REDIGIDO]");
        text = BearerRx().Replace(text, "Bearer [REDIGIDO]");
        text = KeyValueRx().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "[REDIGIDO]");
        return text;
    }

    public static string Tail(string? text, int max)
    {
        var t = Clean(text).Trim();
        return t.Length <= max ? t : "…" + t[^max..];
    }

    public static string Head(string? text, int max)
    {
        var t = Clean(text).Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }
}

/// <summary>Barreiras de segurança: repositórios autorizados, git sem ações destrutivas, caminhos confinados.</summary>
public sealed class Guard(string allowFile)
{
    public static string DefaultAllowFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orq", "repos-autorizados.txt");

    private static string Norm(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    public IReadOnlyList<string> Allowed() =>
        File.Exists(allowFile)
            ? File.ReadAllLines(allowFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
            : new List<string>();

    public bool IsAllowed(string repo) =>
        Allowed().Any(a => string.Equals(a, Norm(repo), StringComparison.OrdinalIgnoreCase));

    public void Allow(string repo)
    {
        if (IsAllowed(repo)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(allowFile)!);
        File.AppendAllLines(allowFile, new[] { Norm(repo) });
    }

    public void EnsureAllowed(string repo)
    {
        if (!IsAllowed(repo))
            throw new PermissionDeniedException(
                $"Repositório não autorizado: {Norm(repo)}. Autorize explicitamente com: orq allow --repo \"{Norm(repo)}\"");
    }

    private static readonly HashSet<string> GitAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "rev-parse", "rev-list", "status", "add", "commit", "worktree", "merge", "diff", "log", "branch", "restore", "show",
    };

    private static readonly HashSet<string> GitForbiddenFlags = new(StringComparer.Ordinal)
    {
        "--force", "-f", "-D", "-d", "--delete", "--hard", "--mirror", "--prune", "-M", "-m:branch",
    };

    /// <summary>O orquestrador só executa um conjunto fechado de comandos git; push, reset, clean, remote etc. são recusados.</summary>
    public static void CheckGit(IReadOnlyList<string> args)
    {
        if (args.Count == 0) throw new PermissionDeniedException("Comando git vazio.");
        var sub = args[0];
        if (!GitAllowed.Contains(sub))
            throw new PermissionDeniedException($"Ação bloqueada: 'git {sub}' não é permitido ao orquestrador sem autorização.");
        foreach (var a in args.Skip(1).TakeWhile(a => a != "--"))
            if (GitForbiddenFlags.Contains(a))
                throw new PermissionDeniedException($"Ação bloqueada: 'git {sub} {a}' é destrutivo e exige autorização.");
        if (sub.Equals("worktree", StringComparison.OrdinalIgnoreCase) &&
            (args.Count < 2 || (args[1] != "add" && args[1] != "list")))
            throw new PermissionDeniedException("Ação bloqueada: só 'git worktree add/list' é permitido.");
        if (sub.Equals("branch", StringComparison.OrdinalIgnoreCase) && args.Skip(1).Any(a => a is "-m" or "--move"))
            throw new PermissionDeniedException("Ação bloqueada: renomear branch exige autorização.");
    }

    public static string EnsureInside(string root, string path)
    {
        var r = Norm(root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            throw new PermissionDeniedException($"Caminho fora do diretório autorizado: {path}");
        return full;
    }
}

/// <summary>Reserva de arquivos: padrões exatos, diretórios (terminados em /) ou com curinga *.</summary>
public static class Scope
{
    private static string N(string p) => p.Replace('\\', '/').TrimStart('.', '/').ToLowerInvariant();

    public static bool IsLiteral(string pattern) => !pattern.Contains('*') && !pattern.EndsWith('/') && !pattern.EndsWith('\\');

    private static string Prefix(string pattern)
    {
        var p = N(pattern);
        var star = p.IndexOf('*');
        return star >= 0 ? p[..star] : p;
    }

    public static bool Matches(string pattern, string path)
    {
        var p = N(pattern);
        var f = N(path);
        if (p.EndsWith('/')) return f.StartsWith(p, StringComparison.Ordinal);
        if (!p.Contains('*')) return p == f;
        var rx = "^" + Regex.Escape(p).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "$";
        return Regex.IsMatch(f, rx);
    }

    public static bool InScope(IEnumerable<string> patterns, string path) => patterns.Any(p => Matches(p, path));

    public static bool Overlap(string a, string b)
    {
        if (IsLiteral(a) && IsLiteral(b)) return N(a) == N(b);
        if (IsLiteral(a)) return Matches(b, a);
        if (IsLiteral(b)) return Matches(a, b);
        var pa = Prefix(a);
        var pb = Prefix(b);
        return pa.StartsWith(pb, StringComparison.Ordinal) || pb.StartsWith(pa, StringComparison.Ordinal);
    }

    /// <summary>Lista vazia significa "repositório inteiro" e conflita com qualquer outra tarefa de escrita.</summary>
    public static bool Conflict(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b) =>
        a.Count == 0 || b.Count == 0 || a.Any(x => b.Any(y => Overlap(x, y)));
}
