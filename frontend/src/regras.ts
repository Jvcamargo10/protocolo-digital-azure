/** Regras do domínio em TypeScript: espelham o C# (ProtocoloDigital.Core/Dominio) para a demo no navegador. */

export type Secretaria = 'Saude' | 'Educacao' | 'Obras' | 'Fazenda' | 'Assistencia' | 'Ouvidoria';
export type Status = 'Recebido' | 'Encaminhado' | 'EmAnalise' | 'Concluido' | 'Indeferido';

export const SECRETARIAS: Secretaria[] = ['Saude', 'Educacao', 'Obras', 'Fazenda', 'Assistencia', 'Ouvidoria'];

const REGRAS: [Secretaria, number, string[]][] = [
  ['Saude', 5, ['saude', 'ubs', 'posto de saude', 'medicamento', 'vacina', 'consulta', 'exame']],
  ['Educacao', 10, ['escola', 'matricula', 'creche', 'transporte escolar', 'merenda', 'professor']],
  ['Obras', 15, ['buraco', 'calcada', 'pavimentacao', 'asfalto', 'iluminacao', 'poda', 'arvore', 'bueiro']],
  ['Fazenda', 10, ['iptu', 'imposto', 'taxa', 'alvara', 'certidao', 'divida ativa', 'iss']],
  ['Assistencia', 7, ['cadunico', 'cesta basica', 'beneficio', 'auxilio', 'bolsa familia', 'cras']],
];

export function normalizar(texto: string): string {
  return texto.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
}

const contem = (texto: string, palavra: string) => ` ${texto} `.includes(` ${palavra}`);

export function classificar(assunto: string, descricao: string): { secretaria: Secretaria; prazoDias: number } {
  const a = normalizar(assunto);
  const t = `${a} ${normalizar(descricao)}`;
  let melhor: { secretaria: Secretaria; prazoDias: number; pontos: number } = { secretaria: 'Ouvidoria', prazoDias: 10, pontos: 0 };
  for (const [secretaria, prazoDias, palavras] of REGRAS) {
    const pontos = palavras.reduce((s, p) => s + (contem(a, p) ? 3 : 0) + (contem(t, p) ? 1 : 0), 0);
    if (pontos > melhor.pontos) melhor = { secretaria, prazoDias, pontos };
  }
  return { secretaria: melhor.secretaria, prazoDias: melhor.prazoDias };
}

export function somarDiasUteis(inicio: Date, dias: number): Date {
  const d = new Date(inicio);
  while (dias > 0) {
    d.setUTCDate(d.getUTCDate() + 1);
    const semana = d.getUTCDay();
    if (semana !== 0 && semana !== 6) dias--;
  }
  return d;
}

const TRANSICOES: Record<Status, Status[]> = {
  Recebido: ['Encaminhado'],
  Encaminhado: ['EmAnalise', 'Concluido', 'Indeferido'],
  EmAnalise: ['Concluido', 'Indeferido'],
  Concluido: [],
  Indeferido: [],
};

export const podeIr = (de: Status, para: Status) => TRANSICOES[de].includes(para);
export const proximosStatus = (de: Status) => TRANSICOES[de];

const ASSINATURAS: [string, number[]][] = [
  ['application/pdf', [0x25, 0x50, 0x44, 0x46, 0x2d]],
  ['image/png', [0x89, 0x50, 0x4e, 0x47]],
  ['image/jpeg', [0xff, 0xd8, 0xff]],
];
export const TAMANHO_MAXIMO = 10 * 1024 * 1024;

/** Valida pelo conteúdo do arquivo, como o back-end. Retorna o content-type ou lança um erro. */
export function validarAnexo(tamanho: number, inicio: Uint8Array): string {
  if (tamanho === 0) throw new Error('O anexo está vazio');
  if (tamanho > TAMANHO_MAXIMO) throw new Error('O anexo passa de 10 MB');
  const achado = ASSINATURAS.find(([, a]) => a.every((b, i) => inicio[i] === b));
  if (!achado) throw new Error('Formato não aceito: envie PDF, PNG ou JPEG');
  return achado[0];
}

export async function sha256(bytes: ArrayBuffer): Promise<string> {
  const hash = await crypto.subtle.digest('SHA-256', bytes);
  return [...new Uint8Array(hash)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

export const formatarNumero = (ano: number, seq: number) => `${ano}/${String(seq).padStart(6, '0')}`;
