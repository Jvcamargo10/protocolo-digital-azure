import { describe, expect, it } from 'vitest';
import { ProtocoloDemo } from './demo';
import { classificar, podeIr, somarDiasUteis, validarAnexo } from './regras';

const pdf = (nome = 'doc.pdf') => new File(['%PDF-1.4 teste'], nome, { type: 'application/pdf' });

describe('regras (espelho do C#)', () => {
  it('classifica pelo texto, sem acento, com o assunto pesando mais', () => {
    expect(classificar('Buraco na calçada', '').secretaria).toBe('Obras');
    expect(classificar('Taxa do alvará', 'loja ao lado da escola e da creche').secretaria).toBe('Fazenda');
    expect(classificar('Elogio', 'Fui bem atendido').secretaria).toBe('Ouvidoria');
  });

  it('soma só dias úteis', () => {
    expect(somarDiasUteis(new Date('2026-10-23T12:00:00Z'), 2).toISOString()).toBe('2026-10-27T12:00:00.000Z');
  });

  it('valida o anexo pelo conteúdo e respeita a máquina de status', () => {
    expect(validarAnexo(10, new TextEncoder().encode('%PDF-1.7'))).toBe('application/pdf');
    expect(() => validarAnexo(10, new TextEncoder().encode('MZ'))).toThrow('Formato não aceito');
    expect(podeIr('Recebido', 'Concluido')).toBe(false);
    expect(podeIr('Encaminhado', 'Concluido')).toBe(true);
  });
});

describe('fluxo da demo', () => {
  it('abre, triagem assíncrona encaminha uma vez só e o andamento segue as regras', async () => {
    const demo = new ProtocoloDemo(0, () => new Date('2026-10-19T12:00:00Z'), false);
    const p = await demo.abrir({ assunto: 'Segunda via do IPTU', descricao: 'Não recebi o carnê', nome: 'Ana Lima',
      email: 'Ana@Exemplo.com', arquivo: pdf() });
    expect(p.numero).toBe('2026/000001');
    expect(p.status).toBe('Recebido');
    expect(p.anexo.sha256).toHaveLength(64);

    await new Promise((r) => setTimeout(r, 5));
    demo.triar(p.numero); // reentrega da mensagem: não muda nada
    const triado = demo.obter('2026/1')!;
    expect(triado.status).toBe('Encaminhado');
    expect(triado.secretaria).toBe('Fazenda');
    expect(triado.historico).toHaveLength(2);

    expect(() => demo.andamento(p.numero, 'Recebido', 'x')).toThrow();
    expect(demo.andamento(p.numero, 'Concluido', 'Guia enviada por e-mail').status).toBe('Concluido');
  });

  it('recusa anexo que não é PDF/PNG/JPEG', async () => {
    const demo = new ProtocoloDemo(0, () => new Date(), false);
    await expect(demo.abrir({ assunto: 'Poda de árvore', descricao: 'x', nome: 'Ana Lima', email: 'a@b.com',
      arquivo: new File(['MZ executavel'], 'foto.pdf') })).rejects.toThrow('Formato não aceito');
  });
});
