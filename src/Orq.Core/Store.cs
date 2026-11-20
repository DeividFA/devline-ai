using Microsoft.Data.Sqlite;

namespace Orq.Core;

public sealed record AttemptRow(long Id, string TaskId, string Role, string Provider, string Started, string? Ended,
    string? Outcome, long InputTokens, long OutputTokens, double? CostUsd, string? UsageKind, string? SessionId, string? Error);

public sealed record ProviderState(string Name, DateTime? CooldownUntil, string? LastOutcome, string? LastError);

public sealed record EventRow(string Ts, string? TaskId, string Kind, string Message);

/// <summary>Persistência em SQLite: registro de tarefas, tentativas, estado dos provedores e eventos.</summary>
public sealed class Store : IDisposable
{
    private readonly SqliteConnection _c;
    private readonly object _l = new();

    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _c = new SqliteConnection($"Data Source={path};Pooling=False");
        _c.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA busy_timeout=15000;"); // várias frentes (processos) usam o mesmo banco
        Exec("""
            CREATE TABLE IF NOT EXISTS tasks(id TEXT PRIMARY KEY, state TEXT NOT NULL, json TEXT NOT NULL, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS attempts(id INTEGER PRIMARY KEY AUTOINCREMENT, task_id TEXT, role TEXT, provider TEXT,
                started TEXT, ended TEXT, outcome TEXT, input_tokens INTEGER DEFAULT 0, output_tokens INTEGER DEFAULT 0,
                cost_usd REAL, usage_kind TEXT, session_id TEXT, error TEXT);
            CREATE TABLE IF NOT EXISTS providers(name TEXT PRIMARY KEY, cooldown_until TEXT, last_outcome TEXT, last_error TEXT);
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT, task_id TEXT, kind TEXT, message TEXT);
            """);
    }

    private static string Now => DateTime.UtcNow.ToString("o");

    private void Exec(string sql, params (string, object?)[] ps)
    {
        lock (_l)
        {
            using var cmd = Cmd(sql, ps);
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteCommand Cmd(string sql, params (string, object?)[] ps)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] ps)
    {
        lock (_l)
        {
            using var cmd = Cmd(sql, ps);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ---- tarefas
    public void Save(TaskItem t) =>
        Exec("INSERT INTO tasks(id,state,json,updated) VALUES($i,$s,$j,$u) ON CONFLICT(id) DO UPDATE SET state=$s,json=$j,updated=$u",
            ("$i", t.Id), ("$s", t.State.ToString()), ("$j", Json.To(t)), ("$u", Now));

    public List<TaskItem> All() => Query("SELECT json FROM tasks ORDER BY rowid", r => Json.From<TaskItem>(r.GetString(0)));

    public TaskItem? Get(string id) =>
        Query("SELECT json FROM tasks WHERE id=$i", r => Json.From<TaskItem>(r.GetString(0)), ("$i", id)).FirstOrDefault();

    /// <summary>Carrega um plano sem reexecutar o que já existe: tarefas concluídas ficam intactas.</summary>
    public (int Added, int Kept) LoadPlan(PlanFile plan)
    {
        int added = 0, kept = 0;
        foreach (var t in plan.Tasks)
        {
            if (string.IsNullOrWhiteSpace(t.Id)) throw new InvalidOperationException("Tarefa sem id no plano.");
            var old = Get(t.Id);
            if (old == null)
            {
                t.State = TaskState.Pending;
                t.Phase = Phase.Dev;
                Save(t);
                added++;
                continue;
            }
            kept++;
            if (old.State is TaskState.Done or TaskState.Running) continue;
            old.Title = t.Title; old.Objective = t.Objective; old.Acceptance = t.Acceptance; old.Role = t.Role;
            old.Complexity = t.Complexity; old.Files = t.Files; old.DependsOn = t.DependsOn; old.Review = t.Review;
            old.Provider = t.Provider; old.Lane = t.Lane; old.Skills = t.Skills; old.Brief = t.Brief;
            Save(old);
        }
        return (added, kept);
    }

    // ---- tentativas
    public long StartAttempt(string taskId, Role role, string provider)
    {
        lock (_l)
        {
            using var cmd = Cmd("INSERT INTO attempts(task_id,role,provider,started) VALUES($t,$r,$p,$s); SELECT last_insert_rowid();",
                ("$t", taskId), ("$r", role.ToString()), ("$p", provider), ("$s", Now));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void EndAttempt(long id, string outcome, Usage? u, string? sessionId, string? error) =>
        Exec("UPDATE attempts SET ended=$e,outcome=$o,input_tokens=$i,output_tokens=$out,cost_usd=$c,usage_kind=$k,session_id=$s,error=$err WHERE id=$id",
            ("$e", Now), ("$o", outcome), ("$i", u?.Input ?? 0), ("$out", u?.Output ?? 0), ("$c", u?.CostUsd),
            ("$k", (u?.Kind ?? UsageKind.Unknown).ToString()), ("$s", sessionId), ("$err", error == null ? null : Redactor.Head(error, 1000)), ("$id", id));

    public int CloseOpenAttempts(IEnumerable<string>? taskIds = null)
    {
        var n = 0;
        if (taskIds == null)
        {
            lock (_l)
            {
                using var cmd = Cmd("UPDATE attempts SET ended=$e,outcome='Interrupted' WHERE ended IS NULL", ("$e", Now));
                return cmd.ExecuteNonQuery();
            }
        }
        foreach (var id in taskIds)
            lock (_l)
            {
                using var cmd = Cmd("UPDATE attempts SET ended=$e,outcome='Interrupted' WHERE ended IS NULL AND task_id=$t", ("$e", Now), ("$t", id));
                n += cmd.ExecuteNonQuery();
            }
        return n;
    }

    /// <summary>Reabre uma tarefa que falhou ou ficou bloqueada, zerando as tentativas e mantendo a última correção pedida.</summary>
    public TaskItem Reopen(string id)
    {
        var t = Get(id) ?? throw new InvalidOperationException($"Tarefa {id} não existe.");
        if (t.State is TaskState.Done or TaskState.Running)
            throw new InvalidOperationException($"Tarefa {id} está {t.State}; só tarefas não concluídas e paradas podem ser reabertas.");
        t.State = TaskState.Interrupted;
        t.Attempts = 0;
        t.NextAction = "reaberta manualmente";
        Save(t);
        return t;
    }

    public List<AttemptRow> Attempts() => Query(
        "SELECT id,task_id,role,provider,started,ended,outcome,input_tokens,output_tokens,cost_usd,usage_kind,session_id,error FROM attempts ORDER BY id",
        r => new AttemptRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), S(r, 5), S(r, 6),
            r.GetInt64(7), r.GetInt64(8), r.IsDBNull(9) ? null : r.GetDouble(9), S(r, 10), S(r, 11), S(r, 12)));

    // ---- provedores
    public void SetProvider(string name, DateTime? cooldownUntil, string? outcome, string? error) =>
        Exec("INSERT INTO providers(name,cooldown_until,last_outcome,last_error) VALUES($n,$c,$o,$e) ON CONFLICT(name) DO UPDATE SET cooldown_until=$c,last_outcome=$o,last_error=$e",
            ("$n", name), ("$c", cooldownUntil?.ToString("o")), ("$o", outcome), ("$e", error == null ? null : Redactor.Head(error, 500)));

    public ProviderState? GetProvider(string name) => Query(
        "SELECT name,cooldown_until,last_outcome,last_error FROM providers WHERE name=$n",
        r => new ProviderState(r.GetString(0),
            S(r, 1) is { } c ? DateTime.Parse(c, null, System.Globalization.DateTimeStyles.RoundtripKind) : null, S(r, 2), S(r, 3)),
        ("$n", name)).FirstOrDefault();

    // ---- eventos
    public void Event(string? taskId, string kind, string message) =>
        Exec("INSERT INTO events(ts,task_id,kind,message) VALUES($t,$i,$k,$m)",
            ("$t", Now), ("$i", taskId), ("$k", kind), ("$m", Redactor.Head(message, 2000)));

    public List<EventRow> Events(int last = 50)
    {
        var rows = Query("SELECT ts,task_id,kind,message FROM events ORDER BY id DESC LIMIT $n",
            r => new EventRow(r.GetString(0), S(r, 1), r.GetString(2), r.GetString(3)), ("$n", last));
        rows.Reverse();
        return rows;
    }

    public void Dispose() => _c.Dispose();
}
