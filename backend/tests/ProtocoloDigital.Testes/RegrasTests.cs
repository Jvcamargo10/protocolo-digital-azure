using ProtocoloDigital.Core.Dominio;

namespace ProtocoloDigital.Testes;

public class RegrasTests
{
    [Theory]
    [InlineData("Buraco na rua", "Tem um buraco enorme na calçada da escola", Secretaria.Obras)]
    [InlineData("Segunda via do IPTU", "Preciso da guia do imposto para pagar", Secretaria.Fazenda)]
    [InlineData("Vaga em creche", "Matrícula do meu filho de 2 anos", Secretaria.Educacao)]
    [InlineData("Falta de medicamentos", "A UBS do bairro está sem remédio", Secretaria.Saude)]
    [InlineData("Atualização do CadÚnico", "Mudei de endereço", Secretaria.Assistencia)]
    [InlineData("Elogio ao atendimento", "Fui muito bem atendido", Secretaria.Ouvidoria)]
    public void TriagemClassificaPeloTextoSemAcento(string assunto, string descricao, Secretaria esperada)
    {
        Assert.Equal(esperada, Triagem.Classificar(assunto, descricao).Secretaria);
    }

    [Fact]
    public void AssuntoPesaMaisQueDescricao()
    {
        var (secretaria, _) = Triagem.Classificar("Taxa do alvará", "Minha loja fica ao lado da escola e da creche");
        Assert.Equal(Secretaria.Fazenda, secretaria);
    }

    [Fact]
    public void PrazoContaSoDiasUteis()
    {
        var sexta = new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.FromHours(-3));
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.FromHours(-3)), Triagem.SomarDiasUteis(sexta, 2));
    }

    [Theory]
    [InlineData(StatusProtocolo.Recebido, StatusProtocolo.Encaminhado, true)]
    [InlineData(StatusProtocolo.Encaminhado, StatusProtocolo.Concluido, true)]
    [InlineData(StatusProtocolo.Recebido, StatusProtocolo.Concluido, false)]
    [InlineData(StatusProtocolo.Concluido, StatusProtocolo.EmAnalise, false)]
    public void TransicoesDeStatus(StatusProtocolo de, StatusProtocolo para, bool permitido)
    {
        Assert.Equal(permitido, MaquinaDeStatus.Pode(de, para));
    }

    [Fact]
    public void ValidaAnexoPeloConteudo()
    {
        Assert.Equal("application/pdf", ValidadorDeArquivo.Validar(100, "%PDF-1.7"u8));
        Assert.Equal("image/png", ValidadorDeArquivo.Validar(100, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D }));
        Assert.Throws<ErroDeNegocio>(() => ValidadorDeArquivo.Validar(100, "MZ\x90\0"u8)); // executável renomeado
        Assert.Throws<ErroDeNegocio>(() => ValidadorDeArquivo.Validar(ValidadorDeArquivo.TamanhoMaximo + 1, "%PDF-"u8));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("Comprovante de endereço.pdf", "Comprovante_de_endere_o.pdf")]
    [InlineData("...", "anexo")]
    public void NomeDoArquivoEhSanitizado(string nome, string esperado)
    {
        Assert.Equal(esperado, ValidadorDeArquivo.NomeSeguro(nome));
    }

    [Theory]
    [InlineData("2026/000123", true, 2026, 123)]
    [InlineData("2026-45", true, 2026, 45)]
    [InlineData("abc", false, 0, 0)]
    [InlineData("2026/0", false, 0, 0)]
    public void LeNumeroDoProtocolo(string texto, bool valido, int ano, int seq)
    {
        Assert.Equal(valido, NumeroProtocolo.TentarLer(texto, out var n));
        if (valido) Assert.Equal(new NumeroProtocolo(ano, seq), n);
    }
}
