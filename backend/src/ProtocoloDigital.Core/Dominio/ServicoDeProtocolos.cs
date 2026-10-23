using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace ProtocoloDigital.Core.Dominio;

public sealed record NovoProtocolo(string Assunto, string Descricao, string CidadaoNome, string CidadaoEmail, string NomeArquivo);

public sealed record ResultadoVerificacao(int Atrasados, int Reenviados);

/// <summary>
/// Casos de uso do protocolo digital.
///
/// Fluxo: Abrir (HTTP) grava o protocolo e põe o número na fila "triagem" → a Function de triagem
/// classifica e publica "ENCAMINHADO" no tópico "protocolos" → cada secretaria e o notificador
/// recebem pela sua assinatura (subscription) do tópico.
/// </summary>
public sealed class ServicoDeProtocolos(
    IRepositorioProtocolos repositorio,
    IArquivos arquivos,
    IMensageria mensageria,
    TimeProvider relogio,
    ILogger<ServicoDeProtocolos> log)
{
    /// <summary>Protocolo que ficou em "Recebido" por mais que isso é reenviado para a triagem.</summary>
    public static readonly TimeSpan TempoMaximoSemTriagem = TimeSpan.FromMinutes(10);

    public async Task<Protocolo> AbrirAsync(NovoProtocolo dados, Stream anexo, long tamanho, CancellationToken ct = default)
    {
        ValidarDados(dados);

        // Lê o anexo uma vez (até 10 MB): valida a assinatura e calcula o SHA-256 para prova de integridade.
        using var memoria = new MemoryStream();
        await anexo.CopyToAsync(memoria, ct);
        var bytes = memoria.ToArray();
        var contentType = ValidadorDeArquivo.Validar(bytes.LongLength, bytes.AsSpan(0, Math.Min(bytes.Length, 8)));
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var agora = relogio.GetUtcNow();
        var numero = await repositorio.ProximoNumeroAsync(agora.Year, ct);
        memoria.Position = 0;
        var blob = await arquivos.GuardarAsync(numero, dados.NomeArquivo, contentType, memoria, ct);

        var protocolo = new Protocolo
        {
            Numero = numero,
            Assunto = dados.Assunto.Trim(),
            Descricao = dados.Descricao.Trim(),
            CidadaoNome = dados.CidadaoNome.Trim(),
            CidadaoEmail = dados.CidadaoEmail.Trim().ToLowerInvariant(),
            Anexo = blob,
            AnexoSha256 = sha256,
            AnexoBytes = bytes.LongLength,
            CriadoEm = agora,
            Historico = [new Andamento(agora, StatusProtocolo.Recebido, "Pedido recebido")],
        };
        await repositorio.SalvarNovoAsync(protocolo, ct);

        // Se o envio falhar aqui, o protocolo fica "Recebido" e a verificação periódica o reenvia.
        try
        {
            await mensageria.EnviarParaTriagemAsync(numero, ct);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Protocolo {Numero} salvo, mas não enviado para triagem; será reenviado", numero);
        }
        log.LogInformation("Protocolo {Numero} aberto ({Bytes} bytes, sha256 {Hash})", numero, bytes.LongLength, sha256);
        return protocolo;
    }

    /// <summary>Idempotente: se a mensagem chegar duas vezes, a segunda não faz nada.</summary>
    public async Task<Protocolo?> TriarAsync(NumeroProtocolo numero, CancellationToken ct = default)
    {
        var protocolo = await repositorio.ObterAsync(numero, ct);
        if (protocolo is null)
        {
            log.LogWarning("Protocolo {Numero} não existe; mensagem descartada", numero);
            return null;
        }
        if (protocolo.Status != StatusProtocolo.Recebido)
        {
            log.LogInformation("Protocolo {Numero} já triado; mensagem repetida ignorada", numero);
            return protocolo;
        }

        var (secretaria, prazoDias) = Triagem.Classificar(protocolo.Assunto, protocolo.Descricao);
        var agora = relogio.GetUtcNow();
        protocolo.Status = StatusProtocolo.Encaminhado;
        protocolo.Secretaria = secretaria;
        protocolo.PrazoEm = Triagem.SomarDiasUteis(agora, prazoDias);
        protocolo.Historico.Add(new Andamento(agora, StatusProtocolo.Encaminhado, $"Encaminhado para {secretaria}"));
        await repositorio.AtualizarAsync(protocolo, ct);
        await PublicarAsync("ENCAMINHADO", protocolo, ct);
        return protocolo;
    }

    public async Task<Protocolo> RegistrarAndamentoAsync(NumeroProtocolo numero, StatusProtocolo novo, string descricao,
        CancellationToken ct = default)
    {
        var protocolo = await repositorio.ObterAsync(numero, ct) ?? throw new ErroDeNegocio("Protocolo não encontrado");
        MaquinaDeStatus.Exigir(protocolo.Status, novo);
        if (string.IsNullOrWhiteSpace(descricao)) throw new ErroDeNegocio("Descreva o andamento");

        protocolo.Status = novo;
        protocolo.Historico.Add(new Andamento(relogio.GetUtcNow(), novo, descricao.Trim()));
        await repositorio.AtualizarAsync(protocolo, ct);
        await PublicarAsync(novo.ToString().ToUpperInvariant(), protocolo, ct);
        return protocolo;
    }

    /// <summary>Execução periódica: marca prazos vencidos e reenvia protocolos que não chegaram à triagem.</summary>
    public async Task<ResultadoVerificacao> VerificarAsync(CancellationToken ct = default)
    {
        var agora = relogio.GetUtcNow();
        int atrasados = 0, reenviados = 0;
        foreach (var p in await repositorio.EmAbertoAsync(ct))
        {
            if (p.Status == StatusProtocolo.Recebido && agora - p.CriadoEm > TempoMaximoSemTriagem)
            {
                await mensageria.EnviarParaTriagemAsync(p.Numero, ct);
                reenviados++;
            }
            else if (p.PrazoEm < agora && !p.Atrasado)
            {
                p.Atrasado = true;
                try
                {
                    await repositorio.AtualizarAsync(p, ct);
                }
                catch (ConflitoDeVersao)
                {
                    continue; // alguém atualizou agora; a próxima execução reavalia
                }
                await PublicarAsync("PRAZO_VENCIDO", p, ct);
                atrasados++;
            }
        }
        return new ResultadoVerificacao(atrasados, reenviados);
    }

    private Task PublicarAsync(string tipo, Protocolo p, CancellationToken ct) =>
        mensageria.PublicarAsync(new EventoProtocolo($"{p.Numero}:{tipo}", tipo, p.Numero.ToString(), p.Secretaria, p.Status,
            p.CidadaoEmail, p.Assunto, relogio.GetUtcNow()), ct);

    private static void ValidarDados(NovoProtocolo d)
    {
        if (string.IsNullOrWhiteSpace(d.Assunto) || d.Assunto.Trim().Length is < 5 or > 120)
            throw new ErroDeNegocio("O assunto precisa ter entre 5 e 120 caracteres");
        if (string.IsNullOrWhiteSpace(d.Descricao) || d.Descricao.Trim().Length > 4000)
            throw new ErroDeNegocio("Descreva o pedido (até 4.000 caracteres)");
        if (string.IsNullOrWhiteSpace(d.CidadaoNome) || d.CidadaoNome.Trim().Length < 3)
            throw new ErroDeNegocio("Informe o nome completo");
        if (!MailAddress.TryCreate(d.CidadaoEmail?.Trim(), out _))
            throw new ErroDeNegocio("E-mail inválido");
    }
}
