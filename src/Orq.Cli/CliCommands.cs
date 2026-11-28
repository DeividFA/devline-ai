using Orq.Core;

public static class CliCommands
{
    public static TaskItem Corrigir(Store store, string id, string? texto, string? arquivo, bool forcar = false)
    {
        if ((texto == null) == (arquivo == null))
            throw new ArgumentException("informe exatamente uma opção: --texto <instrução> ou --arquivo <caminho.md>");
        var instrucao = arquivo == null ? texto! : File.ReadAllText(arquivo);
        return store.Corrigir(id, instrucao, forcar);
    }
}
