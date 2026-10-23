using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ProtocoloDigital.Core.Dominio;

namespace ProtocoloDigital.Testes;

/// <summary>Casos de uso com dublês em memória: rápidos e sem dependência de Azure.</summary>
public class ServicoTests
{
    private readonly FakeTimeProvider relogio = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)); // segunda
    private readonly RepositorioEmMemoria repositorio = new();
    private readonly MensageriaEmMemoria mensageria = new();
    private readonly ServicoDeProtocolos servico;

    public ServicoTests()
    {
        servico = new ServicoDeProtocolos(repositorio, new ArquivosEmMemoria(), mensageria, relogio,
            NullLogger<ServicoDeProtocolos>.Instance);
    }

    private static MemoryStream Pdf() => new(Encoding.ASCII.GetBytes("%PDF-1.7 conteúdo de teste"));

    private Task<Protocolo> Abrir(string assunto = "Buraco na rua", string descricao = "Rua das Flores, 120") =>
        servico.AbrirAsync(new NovoProtocolo(assunto, descricao, "Maria Souza", "Maria@Exemplo.com", "foto.pdf"), Pdf(), 26);

    [Fact]
    public async Task AbrirGravaNumeraEEnviaParaTriagem()
    {
        var p = await Abrir();
        Assert.Equal("2026/000001", p.Numero.ToString());
        Assert.Equal("maria@exemplo.com", p.CidadaoEmail);
        Assert.Equal(64, p.AnexoSha256.Length);
        Assert.Equal(new[] { p.Numero }, mensageria.Triagem);
    }

    [Fact]
    public async Task AnexoInvalidoNaoGeraProtocolo()
    {
        await Assert.ThrowsAsync<ErroDeNegocio>(() => servico.AbrirAsync(
            new NovoProtocolo("Buraco na rua", "x", "Maria Souza", "m@e.com", "virus.pdf"), new MemoryStream("MZ"u8.ToArray()), 2));
        Assert.Empty(repositorio.Dados);
    }

    [Fact]
    public async Task TriagemEncaminhaComPrazoEPublicaEvento()
    {
        var p = await Abrir();
        var triado = await servico.TriarAsync(p.Numero);

        Assert.Equal(StatusProtocolo.Encaminhado, triado!.Status);
        Assert.Equal(Secretaria.Obras, triado.Secretaria);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), triado.PrazoEm); // 15 dias úteis
        Assert.Equal("ENCAMINHADO", Assert.Single(mensageria.Eventos).Tipo);
    }

    [Fact]
    public async Task TriagemRepetidaNaoFazNadaDeNovo()
    {
        var p = await Abrir();
        await servico.TriarAsync(p.Numero);
        await servico.TriarAsync(p.Numero); // o Service Bus pode entregar a mesma mensagem duas vezes
        Assert.Single(mensageria.Eventos);
        Assert.Equal(2, (await repositorio.ObterAsync(p.Numero))!.Historico.Count);
    }

    [Fact]
    public async Task AndamentoRespeitaAMaquinaDeStatus()
    {
        var p = await Abrir();
        await Assert.ThrowsAsync<ErroDeNegocio>(() => servico.RegistrarAndamentoAsync(p.Numero, StatusProtocolo.Concluido, "ok"));
        await servico.TriarAsync(p.Numero);
        var concluido = await servico.RegistrarAndamentoAsync(p.Numero, StatusProtocolo.Concluido, "Buraco tapado");
        Assert.True(concluido.Finalizado);
        Assert.Equal("CONCLUIDO", mensageria.Eventos.Last().Tipo);
    }

    [Fact]
    public async Task VerificacaoMarcaAtrasadosUmaVezEReenviaOsQueNaoForamTriados()
    {
        var triado = await Abrir("Vacina em falta", "UBS Centro");      // Saúde: 5 dias úteis
        await servico.TriarAsync(triado.Numero);
        mensageria.Falhar = true;
        var perdido = await Abrir();                                    // salvo, mas a fila falhou
        mensageria.Falhar = false;
        Assert.Equal(StatusProtocolo.Recebido, perdido.Status);

        relogio.Advance(TimeSpan.FromDays(10));
        var r1 = await servico.VerificarAsync();
        var r2 = await servico.VerificarAsync();

        Assert.Equal(new ResultadoVerificacao(Atrasados: 1, Reenviados: 1), r1);
        Assert.Equal(0, r2.Atrasados);
        Assert.Contains(perdido.Numero, mensageria.Triagem);
        Assert.Contains(mensageria.Eventos, e => e.Tipo == "PRAZO_VENCIDO" && e.Numero == triado.Numero.ToString());
    }

    [Fact]
    public async Task ConflitoDeVersaoEhDetectado()
    {
        var p = await Abrir();
        var copiaA = await repositorio.ObterAsync(p.Numero);
        var copiaB = await repositorio.ObterAsync(p.Numero);
        copiaA!.Atrasado = true;
        await repositorio.AtualizarAsync(copiaA);
        copiaB!.Atrasado = true;
        await Assert.ThrowsAsync<ConflitoDeVersao>(() => repositorio.AtualizarAsync(copiaB));
    }
}

internal sealed class RepositorioEmMemoria : IRepositorioProtocolos
{
    public ConcurrentDictionary<NumeroProtocolo, (Protocolo Dados, int Versao)> Dados { get; } = new();
    private int sequencia;

    public Task<NumeroProtocolo> ProximoNumeroAsync(int ano, CancellationToken ct = default) =>
        Task.FromResult(new NumeroProtocolo(ano, Interlocked.Increment(ref sequencia)));

    public Task SalvarNovoAsync(Protocolo p, CancellationToken ct = default)
    {
        p.Versao = "1";
        Dados[p.Numero] = (Copiar(p), 1);
        return Task.CompletedTask;
    }

    public Task<Protocolo?> ObterAsync(NumeroProtocolo n, CancellationToken ct = default) =>
        Task.FromResult(Dados.TryGetValue(n, out var d) ? Copiar(d.Dados, d.Versao) : null);

    public Task AtualizarAsync(Protocolo p, CancellationToken ct = default)
    {
        var atual = Dados[p.Numero];
        if (p.Versao != atual.Versao.ToString()) throw new ConflitoDeVersao();
        p.Versao = (atual.Versao + 1).ToString();
        Dados[p.Numero] = (Copiar(p), atual.Versao + 1);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Protocolo>> ListarAsync(Secretaria? s, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Protocolo>>(Dados.Values.Select(d => Copiar(d.Dados, d.Versao))
            .Where(p => s is null || p.Secretaria == s).ToList());

    public Task<IReadOnlyList<Protocolo>> EmAbertoAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Protocolo>>(Dados.Values.Select(d => Copiar(d.Dados, d.Versao))
            .Where(p => !p.Finalizado).ToList());

    private static Protocolo Copiar(Protocolo p, int? versao = null) => new()
    {
        Numero = p.Numero, Assunto = p.Assunto, Descricao = p.Descricao, CidadaoNome = p.CidadaoNome,
        CidadaoEmail = p.CidadaoEmail, Anexo = p.Anexo, AnexoSha256 = p.AnexoSha256, AnexoBytes = p.AnexoBytes,
        CriadoEm = p.CriadoEm, Status = p.Status, Secretaria = p.Secretaria, PrazoEm = p.PrazoEm, Atrasado = p.Atrasado,
        Historico = [.. p.Historico], Versao = versao?.ToString() ?? p.Versao,
    };
}

internal sealed class ArquivosEmMemoria : IArquivos
{
    public Task<string> GuardarAsync(NumeroProtocolo n, string nome, string tipo, Stream conteudo, CancellationToken ct = default) =>
        Task.FromResult($"{n.Ano}/{n.Sequencia:D6}/{ValidadorDeArquivo.NomeSeguro(nome)}");
}

internal sealed class MensageriaEmMemoria : IMensageria
{
    public List<NumeroProtocolo> Triagem { get; } = [];
    public List<EventoProtocolo> Eventos { get; } = [];
    public bool Falhar { get; set; }

    public Task EnviarParaTriagemAsync(NumeroProtocolo numero, CancellationToken ct = default)
    {
        if (Falhar) throw new InvalidOperationException("Service Bus indisponível");
        Triagem.Add(numero);
        return Task.CompletedTask;
    }

    public Task PublicarAsync(EventoProtocolo evento, CancellationToken ct = default)
    {
        Eventos.Add(evento);
        return Task.CompletedTask;
    }
}
