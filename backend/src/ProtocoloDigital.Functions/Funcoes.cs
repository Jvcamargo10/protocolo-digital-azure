using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using ProtocoloDigital.Core.Dominio;

namespace ProtocoloDigital.Functions;

/// <summary>Endpoints HTTP: o cidadão abre e acompanha; a secretaria lista e registra andamentos.</summary>
public sealed class ProtocolosHttp(ServicoDeProtocolos servico, IRepositorioProtocolos repositorio)
{
    [Function("AbrirProtocolo")]
    public async Task<IActionResult> Abrir(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "protocolos")] HttpRequest req, CancellationToken ct)
    {
        if (!req.HasFormContentType) return Erro("Envie o formulário como multipart/form-data");
        var form = await req.ReadFormAsync(ct);
        var arquivo = form.Files.GetFile("anexo");
        if (arquivo is null) return Erro("Anexe um documento (PDF, PNG ou JPEG)");

        try
        {
            await using var conteudo = arquivo.OpenReadStream();
            var protocolo = await servico.AbrirAsync(new NovoProtocolo(form["assunto"].ToString(), form["descricao"].ToString(),
                form["nome"].ToString(), form["email"].ToString(), arquivo.FileName), conteudo, arquivo.Length, ct);
            return new CreatedResult($"/api/protocolos/{protocolo.Numero}", Resumo(protocolo));
        }
        catch (ErroDeNegocio e)
        {
            return Erro(e.Message);
        }
    }

    [Function("ConsultarProtocolo")]
    public async Task<IActionResult> Consultar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "protocolos/{ano:int}/{sequencia:int}")] HttpRequest req,
        int ano, int sequencia, CancellationToken ct)
    {
        var protocolo = await repositorio.ObterAsync(new NumeroProtocolo(ano, sequencia), ct);
        return protocolo is null ? new NotFoundObjectResult(new { erro = "Protocolo não encontrado" }) : new OkObjectResult(Resumo(protocolo));
    }

    /// <summary>Área da secretaria: exige chave de função (em produção, Entra ID via Easy Auth na frente).</summary>
    [Function("ListarProtocolos")]
    public async Task<IActionResult> Listar(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "secretarias/protocolos")] HttpRequest req, CancellationToken ct)
    {
        Secretaria? filtro = null;
        if (req.Query.TryGetValue("secretaria", out var valor) && !string.IsNullOrEmpty(valor))
        {
            if (!Enum.TryParse<Secretaria>(valor, ignoreCase: true, out var s)) return Erro("Secretaria inválida");
            filtro = s;
        }
        var lista = await repositorio.ListarAsync(filtro, ct);
        return new OkObjectResult(lista.Select(Resumo));
    }

    [Function("RegistrarAndamento")]
    public async Task<IActionResult> Andamento(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "secretarias/protocolos/{ano:int}/{sequencia:int}/andamentos")]
        HttpRequest req, int ano, int sequencia, CancellationToken ct)
    {
        var corpo = await JsonSerializer.DeserializeAsync<PedidoAndamento>(req.Body, JsonSerializerOptions.Web, ct);
        if (corpo is null || !Enum.TryParse<StatusProtocolo>(corpo.Status, ignoreCase: true, out var status))
            return Erro("Informe status e descricao");
        try
        {
            var p = await servico.RegistrarAndamentoAsync(new NumeroProtocolo(ano, sequencia), status, corpo.Descricao ?? "", ct);
            return new OkObjectResult(Resumo(p));
        }
        catch (ErroDeNegocio e)
        {
            return Erro(e.Message);
        }
        catch (ConflitoDeVersao e)
        {
            return new ConflictObjectResult(new { erro = e.Message });
        }
    }

    private sealed record PedidoAndamento(string? Status, string? Descricao);

    private static BadRequestObjectResult Erro(string mensagem) => new(new { erro = mensagem });

    /// <summary>O que sai na API. O nome do blob e o e-mail completo não são expostos.</summary>
    internal static object Resumo(Protocolo p) => new
    {
        numero = p.Numero.ToString(),
        p.Assunto,
        status = p.Status.ToString(),
        secretaria = p.Secretaria?.ToString(),
        p.CriadoEm,
        p.PrazoEm,
        p.Atrasado,
        anexo = new { sha256 = p.AnexoSha256, bytes = p.AnexoBytes },
        historico = p.Historico.Select(h => new { h.Em, status = h.Status.ToString(), h.Descricao }),
    };
}

/// <summary>Consome a fila "triagem". Depois de 5 falhas o Service Bus manda a mensagem para a dead-letter queue.</summary>
public sealed class TriagemFuncao(ServicoDeProtocolos servico, ILogger<TriagemFuncao> log)
{
    [Function("Triagem")]
    public async Task Executar([ServiceBusTrigger("triagem", Connection = "ServiceBus")] ServiceBusReceivedMessage mensagem,
        CancellationToken ct)
    {
        var texto = mensagem.Body.ToString();
        if (!NumeroProtocolo.TentarLer(texto, out var numero))
        {
            // Mensagem malformada nunca vai dar certo: lançar exceção só gastaria as 5 tentativas.
            log.LogError("Mensagem {Id} com número inválido: {Texto}", mensagem.MessageId, texto);
            return;
        }
        await servico.TriarAsync(numero, ct);
    }
}

/// <summary>
/// Assinatura "notificacoes" do tópico: avisa o cidadão a cada mudança. Aqui só registra no log;
/// em produção, chamaria o Azure Communication Services Email.
/// </summary>
public sealed class NotificacaoFuncao(ILogger<NotificacaoFuncao> log)
{
    [Function("NotificarCidadao")]
    public void Executar([ServiceBusTrigger("protocolos", "notificacoes", Connection = "ServiceBus")] ServiceBusReceivedMessage mensagem)
    {
        var evento = mensagem.Body.ToObjectFromJson<EventoProtocolo>(JsonSerializerOptions.Web);
        log.LogInformation("Notificação {Tipo} do protocolo {Numero} para {Email}", evento?.Tipo, evento?.Numero,
            Mascarar(evento?.CidadaoEmail));
    }

    internal static string Mascarar(string? email) =>
        email is null || !email.Contains('@') ? "?" : $"{email[0]}***{email[email.IndexOf('@')..]}";
}

/// <summary>Todo dia às 06h (Brasília): marca prazos vencidos e reenvia o que não chegou à triagem.</summary>
public sealed class VerificacaoFuncao(ServicoDeProtocolos servico, ILogger<VerificacaoFuncao> log)
{
    [Function("VerificarPrazos")]
    public async Task Executar([TimerTrigger("0 0 9 * * *")] TimerInfo timer, CancellationToken ct)
    {
        var r = await servico.VerificarAsync(ct);
        log.LogInformation("Verificação: {Atrasados} atrasados, {Reenviados} reenviados para triagem", r.Atrasados, r.Reenviados);
    }
}
