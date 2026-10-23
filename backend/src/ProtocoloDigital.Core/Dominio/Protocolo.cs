namespace ProtocoloDigital.Core.Dominio;

public enum Secretaria { Saude, Educacao, Obras, Fazenda, Assistencia, Ouvidoria }

public enum StatusProtocolo { Recebido, Encaminhado, EmAnalise, Concluido, Indeferido }

/// <summary>Violação de regra de negócio: vira HTTP 400/409 com mensagem legível.</summary>
public sealed class ErroDeNegocio(string mensagem) : Exception(mensagem);

/// <summary>Número no formato 2026/000123: sequência anual sem buracos visíveis ao cidadão.</summary>
public readonly record struct NumeroProtocolo(int Ano, int Sequencia)
{
    public override string ToString() => $"{Ano}/{Sequencia:D6}";

    public static bool TentarLer(string? texto, out NumeroProtocolo numero)
    {
        numero = default;
        var partes = (texto ?? "").Split('/', '-');
        if (partes.Length != 2 || !int.TryParse(partes[0], out var ano) || !int.TryParse(partes[1], out var seq)
            || ano is < 2000 or > 2100 || seq <= 0)
        {
            return false;
        }
        numero = new NumeroProtocolo(ano, seq);
        return true;
    }
}

public sealed record Andamento(DateTimeOffset Em, StatusProtocolo Status, string Descricao);

public sealed class Protocolo
{
    public required NumeroProtocolo Numero { get; init; }
    public required string Assunto { get; init; }
    public required string Descricao { get; init; }
    public required string CidadaoNome { get; init; }
    public required string CidadaoEmail { get; init; }
    public required string Anexo { get; init; }
    public required string AnexoSha256 { get; init; }
    public required long AnexoBytes { get; init; }
    public required DateTimeOffset CriadoEm { get; init; }

    public StatusProtocolo Status { get; set; } = StatusProtocolo.Recebido;
    public Secretaria? Secretaria { get; set; }
    public DateTimeOffset? PrazoEm { get; set; }
    public bool Atrasado { get; set; }
    public List<Andamento> Historico { get; init; } = [];

    /// <summary>ETag do Table Storage: a gravação falha se outra execução mudou o registro antes.</summary>
    public string? Versao { get; set; }

    public bool Finalizado => Status is StatusProtocolo.Concluido or StatusProtocolo.Indeferido;
}

/// <summary>Evento publicado no tópico do Service Bus a cada mudança relevante.</summary>
public sealed record EventoProtocolo(string Id, string Tipo, string Numero, Secretaria? Secretaria, StatusProtocolo Status,
    string CidadaoEmail, string Assunto, DateTimeOffset Em);
