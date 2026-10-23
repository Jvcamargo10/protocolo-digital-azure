using System.Text;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using ProtocoloDigital.Core.Dominio;
using ProtocoloDigital.Core.Infra;

namespace ProtocoloDigital.Testes;

/// <summary>
/// Testes contra o Azurite (emulador do Storage), que o CI sobe antes dos testes.
/// Localmente: npx azurite --silent --skipApiVersionCheck
/// </summary>
public class AzuriteTests : IAsyncLifetime
{
    private const string Conexao = "UseDevelopmentStorage=true";
    private readonly TabelaProtocolos tabela = new(new TableServiceClient(Conexao));
    private readonly ArquivosBlob arquivos = new(new BlobServiceClient(Conexao));
    private readonly int ano = Random.Shared.Next(2030, 2099); // um contador novo por execução

    public async Task InitializeAsync()
    {
        await tabela.CriarTabelasAsync();
        await arquivos.CriarContainerAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task NumeracaoNaoRepeteComPedidosSimultaneos()
    {
        var numeros = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => tabela.ProximoNumeroAsync(ano)));
        Assert.Equal(Enumerable.Range(1, 25), numeros.Select(n => n.Sequencia).Order());
    }

    [Fact]
    public async Task GravaLeEDetectaConflitoDeVersao()
    {
        var numero = await tabela.ProximoNumeroAsync(ano);
        var p = new Protocolo
        {
            Numero = numero, Assunto = "Poda de árvore", Descricao = "Galho sobre a fiação", CidadaoNome = "João",
            CidadaoEmail = "joao@exemplo.com", Anexo = "x", AnexoSha256 = new string('a', 64), AnexoBytes = 10,
            CriadoEm = DateTimeOffset.UtcNow, Historico = [new Andamento(DateTimeOffset.UtcNow, StatusProtocolo.Recebido, "ok")],
        };
        await tabela.SalvarNovoAsync(p);

        var a = (await tabela.ObterAsync(numero))!;
        var b = (await tabela.ObterAsync(numero))!;
        Assert.Equal("Poda de árvore", a.Assunto);
        Assert.Single(a.Historico);
        Assert.Null(a.Secretaria);

        a.Status = StatusProtocolo.Encaminhado;
        a.Secretaria = Secretaria.Obras;
        await tabela.AtualizarAsync(a);
        b.Atrasado = true;
        await Assert.ThrowsAsync<ConflitoDeVersao>(() => tabela.AtualizarAsync(b));

        var obras = await tabela.ListarAsync(Secretaria.Obras);
        Assert.Contains(obras, x => x.Numero == numero);
    }

    [Fact]
    public async Task AnexoNaoEhSobrescrito()
    {
        var numero = new NumeroProtocolo(ano, Random.Shared.Next(1, 999_999));
        var nome = await arquivos.GuardarAsync(numero, "doc.pdf", "application/pdf", new MemoryStream("%PDF-1"u8.ToArray()));
        Assert.Equal($"{ano}/{numero.Sequencia:D6}/doc.pdf", nome);
        await Assert.ThrowsAsync<RequestFailedException>(() =>
            arquivos.GuardarAsync(numero, "doc.pdf", "application/pdf", new MemoryStream(Encoding.ASCII.GetBytes("%PDF-2"))));
    }
}
