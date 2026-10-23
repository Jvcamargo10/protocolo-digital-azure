using System.Text.Json;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ProtocoloDigital.Core.Dominio;

namespace ProtocoloDigital.Core.Infra;

/// <summary>Anexos no Blob Storage, em container privado. Um blob nunca é sobrescrito.</summary>
public sealed class ArquivosBlob(BlobServiceClient servico) : IArquivos
{
    public const string Container = "anexos";
    private readonly BlobContainerClient container = servico.GetBlobContainerClient(Container);

    public Task CriarContainerAsync(CancellationToken ct = default) => container.CreateIfNotExistsAsync(cancellationToken: ct);

    public async Task<string> GuardarAsync(NumeroProtocolo numero, string nomeArquivo, string contentType, Stream conteudo,
        CancellationToken ct = default)
    {
        var nome = $"{numero.Ano}/{numero.Sequencia:D6}/{ValidadorDeArquivo.NomeSeguro(nomeArquivo)}";
        await container.GetBlobClient(nome).UploadAsync(conteudo, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }, // falha se já existir
        }, ct);
        return nome;
    }
}

/// <summary>
/// Service Bus: a fila "triagem" tem detecção de duplicidade, e o MessageId é o número do protocolo:
/// reenviar o mesmo número dentro da janela de detecção não gera uma segunda mensagem.
/// O tópico "protocolos" leva a secretaria numa propriedade, usada pelos filtros das assinaturas.
/// </summary>
public sealed class MensageriaServiceBus : IMensageria, IAsyncDisposable
{
    public const string FilaTriagem = "triagem";
    public const string TopicoProtocolos = "protocolos";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ServiceBusSender triagem;
    private readonly ServiceBusSender topico;

    public MensageriaServiceBus(ServiceBusClient cliente)
    {
        triagem = cliente.CreateSender(FilaTriagem);
        topico = cliente.CreateSender(TopicoProtocolos);
    }

    public Task EnviarParaTriagemAsync(NumeroProtocolo numero, CancellationToken ct = default) =>
        triagem.SendMessageAsync(new ServiceBusMessage(numero.ToString()) { MessageId = numero.ToString() }, ct);

    public Task PublicarAsync(EventoProtocolo evento, CancellationToken ct = default)
    {
        var mensagem = new ServiceBusMessage(BinaryData.FromObjectAsJson(evento, Json))
        {
            MessageId = evento.Id,
            Subject = evento.Tipo,
            ContentType = "application/json",
        };
        mensagem.ApplicationProperties["Secretaria"] = evento.Secretaria?.ToString() ?? "";
        mensagem.ApplicationProperties["Tipo"] = evento.Tipo;
        return topico.SendMessageAsync(mensagem, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await triagem.DisposeAsync();
        await topico.DisposeAsync();
    }
}
