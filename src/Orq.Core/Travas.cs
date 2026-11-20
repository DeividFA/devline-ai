using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Orq.Core;

/// <summary>Proteções automáticas contra erros que já custaram horas: plano com sinônimos, reserva de arquivos
/// incompleta e espera fixa depois do limite de uso.</summary>
public static class Travas
{
    private static readonly Dictionary<string, string> Complexidades = new(StringComparer.OrdinalIgnoreCase)
    {
        ["small"] = "simple", ["low"] = "simple", ["baixa"] = "simple", ["simples"] = "simple", ["easy"] = "simple",
        ["media"] = "medium", ["média"] = "medium", ["normal"] = "medium",
        ["high"] = "complex", ["alta"] = "complex", ["large"] = "complex", ["hard"] = "complex", ["complexa"] = "complex",
    };

    /// <summary>Troca sinônimos de complexidade pelos valores aceitos; o resto do JSON fica igual.</summary>
    public static string NormalizarPlano(string json)
    {
        var raiz = JsonNode.Parse(json);
        if (raiz?["tasks"] is JsonArray tarefas)
            foreach (var t in tarefas)
                if (t?["complexity"] is JsonValue v && v.TryGetValue<string>(out var c) && Complexidades.TryGetValue(c, out var ok))
                    t["complexity"] = ok;
        return raiz?.ToJsonString() ?? json;
    }

    /// <summary>Todo componente cujo template/estilo está na reserva leva junto o .ts (imports, inputs) e,
    /// se existir, o teste de unidade. Sem isso o orquestrador desfazia o import e o build falhava em loop.</summary>
    public static bool CompletarReserva(TaskItem t, string raizDoRepo)
    {
        var novos = new List<string>();
        foreach (var f in t.Files.ToList())
        {
            var m = Regex.Match(f, @"^(.*\.component)\.(html|scss|css)$");
            if (!m.Success) continue;
            var ts = m.Groups[1].Value + ".ts";
            if (!t.Files.Contains(ts) && !novos.Contains(ts)) novos.Add(ts);
            var spec = m.Groups[1].Value + ".spec.ts";
            if (!t.Files.Contains(spec) && !novos.Contains(spec) && File.Exists(Path.Combine(raizDoRepo, spec))) novos.Add(spec);
        }
        t.Files.AddRange(novos);
        return novos.Count > 0;
    }

    /// <summary>Horário em que o limite de uso volta, lido da mensagem do provedor (ex.: "resets 8:10am",
    /// "try again at 10:35 AM"). Null quando a mensagem não diz.</summary>
    public static DateTime? VoltaDoLimite(string? mensagem, DateTime agoraLocal)
    {
        if (string.IsNullOrWhiteSpace(mensagem)) return null;
        var m = Regex.Match(mensagem, @"(?:resets|try again at)\s+(\d{1,2})(?::(\d{2}))?\s*([ap]\.?m\.?)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) % 12;
        var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (m.Groups[3].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase)) h += 12;
        var alvo = agoraLocal.Date.AddHours(h).AddMinutes(min + 1);
        if (alvo <= agoraLocal) alvo = alvo.AddDays(1);
        return alvo;
    }
}
