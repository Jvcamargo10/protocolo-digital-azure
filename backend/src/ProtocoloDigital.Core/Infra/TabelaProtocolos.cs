using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using ProtocoloDigital.Core.Dominio;

namespace ProtocoloDigital.Core.Infra;

/// <summary>
/// Protocolos no Azure Table Storage.
/// - PartitionKey = ano, RowKey = sequência com zeros à esquerda: busca pelo número é uma leitura pontual,
///   a forma mais barata e rápida do Table Storage.
/// - O contador anual usa concorrência otimista (ETag): duas Functions pedindo número ao mesmo tempo
///   nunca recebem o mesmo; quem perde a disputa tenta de novo.
/// - Consultas por secretaria varrem a tabela. Com volume alto, a evolução seria uma tabela-índice
///   (PartitionKey = secretaria), escrita junto com o protocolo.
/// </summary>
public sealed class TabelaProtocolos(TableServiceClient servico) : IRepositorioProtocolos
{
    private const string TabelaContadores = "contadores";
    private const string TabelaDados = "protocolos";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TableClient contadores = servico.GetTableClient(TabelaContadores);
    private readonly TableClient protocolos = servico.GetTableClient(TabelaDados);

    public async Task CriarTabelasAsync(CancellationToken ct = default)
    {
        await contadores.CreateIfNotExistsAsync(ct);
        await protocolos.CreateIfNotExistsAsync(ct);
    }

    public async Task<NumeroProtocolo> ProximoNumeroAsync(int ano, CancellationToken ct = default)
    {
        var chave = ano.ToString();
        for (var tentativa = 0; tentativa < 50; tentativa++)
        {
            var atual = await contadores.GetEntityIfExistsAsync<TableEntity>("protocolo", chave, cancellationToken: ct);
            try
            {
                if (!atual.HasValue)
                {
                    await contadores.AddEntityAsync(new TableEntity("protocolo", chave) { ["Valor"] = 1 }, ct);
                    return new NumeroProtocolo(ano, 1);
                }
                var entidade = atual.Value!;
                var proximo = entidade.GetInt32("Valor")!.Value + 1;
                entidade["Valor"] = proximo;
                await contadores.UpdateEntityAsync(entidade, entidade.ETag, TableUpdateMode.Replace, ct);
                return new NumeroProtocolo(ano, proximo);
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412)
            {
                // 409: outro criou o contador primeiro. 412: outro incrementou primeiro. Tenta de novo.
                await Task.Delay(Random.Shared.Next(5, 25) * (tentativa + 1), ct);
            }
        }
        throw new InvalidOperationException("Não foi possível obter um número de protocolo");
    }

    public async Task SalvarNovoAsync(Protocolo protocolo, CancellationToken ct = default)
    {
        var resposta = await protocolos.AddEntityAsync(ParaEntidade(protocolo), ct);
        protocolo.Versao = resposta.Headers.ETag?.ToString();
    }

    public async Task<Protocolo?> ObterAsync(NumeroProtocolo numero, CancellationToken ct = default)
    {
        var r = await protocolos.GetEntityIfExistsAsync<TableEntity>(numero.Ano.ToString(), numero.Sequencia.ToString("D6"),
            cancellationToken: ct);
        return r.HasValue ? DeEntidade(r.Value!) : null;
    }

    public async Task AtualizarAsync(Protocolo protocolo, CancellationToken ct = default)
    {
        try
        {
            var etag = protocolo.Versao is null ? ETag.All : new ETag(protocolo.Versao);
            var resposta = await protocolos.UpdateEntityAsync(ParaEntidade(protocolo), etag, TableUpdateMode.Replace, ct);
            protocolo.Versao = resposta.Headers.ETag?.ToString();
        }
        catch (RequestFailedException e) when (e.Status == 412)
        {
            throw new ConflitoDeVersao();
        }
    }

    public async Task<IReadOnlyList<Protocolo>> ListarAsync(Secretaria? secretaria, CancellationToken ct = default)
    {
        var filtro = secretaria is null ? null : TableClient.CreateQueryFilter($"Secretaria eq {secretaria.ToString()}");
        var lista = new List<Protocolo>();
        await foreach (var e in protocolos.QueryAsync<TableEntity>(filtro, cancellationToken: ct))
        {
            lista.Add(DeEntidade(e));
        }
        return lista.OrderByDescending(p => p.CriadoEm).ToList();
    }

    public async Task<IReadOnlyList<Protocolo>> EmAbertoAsync(CancellationToken ct = default)
    {
        var lista = new List<Protocolo>();
        await foreach (var e in protocolos.QueryAsync<TableEntity>("Finalizado eq false", cancellationToken: ct))
        {
            lista.Add(DeEntidade(e));
        }
        return lista;
    }

    internal static TableEntity ParaEntidade(Protocolo p)
    {
        var e = new TableEntity(p.Numero.Ano.ToString(), p.Numero.Sequencia.ToString("D6"))
        {
            ["Assunto"] = p.Assunto,
            ["Descricao"] = p.Descricao,
            ["CidadaoNome"] = p.CidadaoNome,
            ["CidadaoEmail"] = p.CidadaoEmail,
            ["Anexo"] = p.Anexo,
            ["AnexoSha256"] = p.AnexoSha256,
            ["AnexoBytes"] = p.AnexoBytes,
            ["CriadoEm"] = p.CriadoEm,
            ["Status"] = p.Status.ToString(),
            ["Atrasado"] = p.Atrasado,
            ["Finalizado"] = p.Finalizado,
            ["Historico"] = JsonSerializer.Serialize(p.Historico, Json),
        };
        // O Table Storage não guarda propriedades nulas: só grava quando há valor.
        if (p.Secretaria is { } secretaria) e["Secretaria"] = secretaria.ToString();
        if (p.PrazoEm is { } prazo) e["PrazoEm"] = prazo;
        return e;
    }

    internal static Protocolo DeEntidade(TableEntity e) => new()
    {
        Numero = new NumeroProtocolo(int.Parse(e.PartitionKey), int.Parse(e.RowKey)),
        Assunto = e.GetString("Assunto"),
        Descricao = e.GetString("Descricao"),
        CidadaoNome = e.GetString("CidadaoNome"),
        CidadaoEmail = e.GetString("CidadaoEmail"),
        Anexo = e.GetString("Anexo"),
        AnexoSha256 = e.GetString("AnexoSha256"),
        AnexoBytes = e.GetInt64("AnexoBytes") ?? 0,
        CriadoEm = e.GetDateTimeOffset("CriadoEm") ?? DateTimeOffset.MinValue,
        Status = Enum.Parse<StatusProtocolo>(e.GetString("Status")),
        Secretaria = e.GetString("Secretaria") is { } s ? Enum.Parse<Secretaria>(s) : null,
        PrazoEm = e.GetDateTimeOffset("PrazoEm"),
        Atrasado = e.GetBoolean("Atrasado") ?? false,
        Historico = JsonSerializer.Deserialize<List<Andamento>>(e.GetString("Historico") ?? "[]", Json) ?? [],
        Versao = e.ETag.ToString(),
    };
}
