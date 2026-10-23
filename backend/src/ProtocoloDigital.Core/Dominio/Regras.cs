using System.Globalization;
using System.Text;

namespace ProtocoloDigital.Core.Dominio;

/// <summary>
/// Triagem automática: decide a secretaria pelo texto do pedido. Sem palavra conhecida, vai para a
/// Ouvidoria, que faz a triagem manual. Cada secretaria tem um prazo de resposta (SLA) em dias úteis.
/// </summary>
public static class Triagem
{
    private static readonly (Secretaria Secretaria, int PrazoDias, string[] Palavras)[] Regras =
    [
        (Secretaria.Saude, 5, ["saude", "ubs", "posto de saude", "medicamento", "vacina", "consulta", "exame"]),
        (Secretaria.Educacao, 10, ["escola", "matricula", "creche", "transporte escolar", "merenda", "professor"]),
        (Secretaria.Obras, 15, ["buraco", "calcada", "pavimentacao", "asfalto", "iluminacao", "poda", "arvore", "bueiro"]),
        (Secretaria.Fazenda, 10, ["iptu", "imposto", "taxa", "alvara", "certidao", "divida ativa", "iss"]),
        (Secretaria.Assistencia, 7, ["cadunico", "cesta basica", "beneficio", "auxilio", "bolsa familia", "cras"]),
    ];

    public const int PrazoOuvidoria = 10;

    public static (Secretaria Secretaria, int PrazoDias) Classificar(string assunto, string descricao)
    {
        var assuntoNormalizado = Normalizar(assunto);
        var texto = assuntoNormalizado + " " + Normalizar(descricao);
        // O assunto pesa mais que a descrição: quem diz "IPTU" no assunto quer a Fazenda, mesmo citando a escola.
        var melhor = Regras
            .Select(r => (r.Secretaria, r.PrazoDias,
                Pontos: r.Palavras.Sum(p => (Contem(assuntoNormalizado, p) ? 3 : 0) + (Contem(texto, p) ? 1 : 0))))
            .Where(r => r.Pontos > 0)
            .OrderByDescending(r => r.Pontos)
            .FirstOrDefault();
        return melhor.Pontos > 0 ? (melhor.Secretaria, melhor.PrazoDias) : (Secretaria.Ouvidoria, PrazoOuvidoria);
    }

    private static bool Contem(string texto, string palavra) =>
        $" {texto} ".Contains($" {palavra}", StringComparison.Ordinal);

    internal static string Normalizar(string texto)
    {
        var decomposto = texto.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposto.Length);
        foreach (var c in decomposto)
        {
            var categoria = CharUnicodeInfo.GetUnicodeCategory(c);
            if (categoria == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Soma dias úteis (segunda a sexta). Feriados ficam de fora para manter o exemplo simples.</summary>
    public static DateTimeOffset SomarDiasUteis(DateTimeOffset inicio, int dias)
    {
        var data = inicio;
        while (dias > 0)
        {
            data = data.AddDays(1);
            if (data.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) dias--;
        }
        return data;
    }
}

/// <summary>Transições permitidas. Protocolo finalizado não muda mais.</summary>
public static class MaquinaDeStatus
{
    private static readonly Dictionary<StatusProtocolo, StatusProtocolo[]> Permitidas = new()
    {
        [StatusProtocolo.Recebido] = [StatusProtocolo.Encaminhado],
        [StatusProtocolo.Encaminhado] = [StatusProtocolo.EmAnalise, StatusProtocolo.Concluido, StatusProtocolo.Indeferido],
        [StatusProtocolo.EmAnalise] = [StatusProtocolo.Concluido, StatusProtocolo.Indeferido],
        [StatusProtocolo.Concluido] = [],
        [StatusProtocolo.Indeferido] = [],
    };

    public static bool Pode(StatusProtocolo de, StatusProtocolo para) => Permitidas[de].Contains(para);

    public static void Exigir(StatusProtocolo de, StatusProtocolo para)
    {
        if (!Pode(de, para)) throw new ErroDeNegocio($"Não é possível passar de {de} para {para}");
    }
}

/// <summary>Valida o anexo pelo conteúdo (assinatura do arquivo), não só pela extensão informada.</summary>
public static class ValidadorDeArquivo
{
    public const long TamanhoMaximo = 10 * 1024 * 1024;

    private static readonly (string Tipo, byte[] Assinatura)[] Tipos =
    [
        ("application/pdf", "%PDF-"u8.ToArray()),
        ("image/png", [0x89, 0x50, 0x4E, 0x47]),
        ("image/jpeg", [0xFF, 0xD8, 0xFF]),
    ];

    /// <summary>Retorna o content-type detectado, ou lança ErroDeNegocio.</summary>
    public static string Validar(long tamanho, ReadOnlySpan<byte> inicio)
    {
        if (tamanho == 0) throw new ErroDeNegocio("O anexo está vazio");
        if (tamanho > TamanhoMaximo) throw new ErroDeNegocio("O anexo passa de 10 MB");
        foreach (var (tipo, assinatura) in Tipos)
        {
            if (inicio.StartsWith(assinatura)) return tipo;
        }
        throw new ErroDeNegocio("Formato não aceito: envie PDF, PNG ou JPEG");
    }

    /// <summary>Nome seguro para o blob: sem caminhos, só letras, números, ponto, hífen e sublinhado.</summary>
    public static string NomeSeguro(string nome)
    {
        var baseName = Path.GetFileName(nome);
        var limpo = new string(baseName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return string.IsNullOrWhiteSpace(limpo.Trim('.', '_')) ? "anexo" : limpo[..Math.Min(limpo.Length, 80)];
    }
}
