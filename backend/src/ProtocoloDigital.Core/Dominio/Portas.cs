namespace ProtocoloDigital.Core.Dominio;

/// <summary>Persistência dos protocolos (Azure Table Storage em produção).</summary>
public interface IRepositorioProtocolos
{
    /// <summary>Próximo número do ano. Seguro com várias instâncias da Function executando ao mesmo tempo.</summary>
    Task<NumeroProtocolo> ProximoNumeroAsync(int ano, CancellationToken ct = default);
    Task SalvarNovoAsync(Protocolo protocolo, CancellationToken ct = default);
    Task<Protocolo?> ObterAsync(NumeroProtocolo numero, CancellationToken ct = default);
    /// <summary>Grava com controle de concorrência otimista (ETag). Lança ConflitoDeVersao se outro alterou antes.</summary>
    Task AtualizarAsync(Protocolo protocolo, CancellationToken ct = default);
    Task<IReadOnlyList<Protocolo>> ListarAsync(Secretaria? secretaria, CancellationToken ct = default);
    Task<IReadOnlyList<Protocolo>> EmAbertoAsync(CancellationToken ct = default);
}

public sealed class ConflitoDeVersao() : Exception("O protocolo foi alterado por outra operação");

/// <summary>Armazenamento dos anexos (Blob Storage).</summary>
public interface IArquivos
{
    Task<string> GuardarAsync(NumeroProtocolo numero, string nomeArquivo, string contentType, Stream conteudo,
        CancellationToken ct = default);
}

/// <summary>Mensageria (Service Bus): fila de triagem e tópico de eventos.</summary>
public interface IMensageria
{
    Task EnviarParaTriagemAsync(NumeroProtocolo numero, CancellationToken ct = default);
    Task PublicarAsync(EventoProtocolo evento, CancellationToken ct = default);
}
