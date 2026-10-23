/**
 * Simulação do back-end para a demo no GitHub Pages. Mantém o mesmo fluxo assíncrono do Azure:
 * a Function HTTP grava e põe o número na fila; a Function de triagem consome a fila depois;
 * os eventos do tópico chegam às assinaturas (secretaria e notificações).
 */
import { classificar, formatarNumero, podeIr, sha256, somarDiasUteis, validarAnexo, type Secretaria, type Status } from './regras';

export interface Andamento { em: string; status: Status; descricao: string; }
export interface Protocolo {
  numero: string;
  assunto: string;
  descricao: string;
  cidadaoNome: string;
  cidadaoEmail: string;
  status: Status;
  secretaria: Secretaria | null;
  criadoEm: string;
  prazoEm: string | null;
  atrasado: boolean;
  anexo: { nome: string; bytes: number; sha256: string; tipo: string };
  historico: Andamento[];
}

/** Uma linha do "monitor" que mostra por onde a requisição passou no Azure. */
export interface Passo { em: string; recurso: string; detalhe: string; tipo: 'http' | 'storage' | 'fila' | 'topico' | 'funcao'; }

export interface NovoPedido { assunto: string; descricao: string; nome: string; email: string; arquivo: File; }

type Ouvinte = () => void;

export class ProtocoloDemo {
  private protocolos = new Map<string, Protocolo>();
  private sequencia = 0;
  private passos: Passo[] = [];
  private ouvintes = new Set<Ouvinte>();

  constructor(private readonly atrasoFila = 900, private readonly agora: () => Date = () => new Date(), semear = true) {
    if (semear) this.semear();
  }

  aoMudar(o: Ouvinte) { this.ouvintes.add(o); return () => { this.ouvintes.delete(o); }; }
  private avisar() { this.ouvintes.forEach((o) => o()); }
  private registrar(recurso: string, detalhe: string, tipo: Passo['tipo']) {
    this.passos.unshift({ em: this.agora().toISOString(), recurso, detalhe, tipo });
    this.passos = this.passos.slice(0, 40);
    this.avisar();
  }

  monitor() { return this.passos; }
  listar(secretaria?: Secretaria) {
    return [...this.protocolos.values()].filter((p) => !secretaria || p.secretaria === secretaria)
      .sort((a, b) => b.criadoEm.localeCompare(a.criadoEm));
  }
  obter(numero: string) {
    const [ano, seq] = numero.trim().split(/[/-]/).map(Number);
    return ano && seq ? this.protocolos.get(formatarNumero(ano, seq)) ?? null : null;
  }

  async abrir(pedido: NovoPedido): Promise<Protocolo> {
    if (pedido.assunto.trim().length < 5) throw new Error('O assunto precisa ter pelo menos 5 caracteres');
    if (!pedido.descricao.trim()) throw new Error('Descreva o pedido');
    if (pedido.nome.trim().length < 3) throw new Error('Informe o nome completo');
    if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(pedido.email.trim())) throw new Error('E-mail inválido');
    const bytes = await pedido.arquivo.arrayBuffer();
    const tipo = validarAnexo(bytes.byteLength, new Uint8Array(bytes.slice(0, 8)));
    const hash = await sha256(bytes);

    const agora = this.agora();
    const numero = formatarNumero(agora.getFullYear(), ++this.sequencia);
    this.registrar('HTTP Function · AbrirProtocolo', `POST /api/protocolos → ${numero}`, 'http');
    this.registrar('Blob Storage · anexos', `${numero}/${pedido.arquivo.name} (${bytes.byteLength} bytes)`, 'storage');
    const p: Protocolo = {
      numero, assunto: pedido.assunto.trim(), descricao: pedido.descricao.trim(), cidadaoNome: pedido.nome.trim(),
      cidadaoEmail: pedido.email.trim().toLowerCase(), status: 'Recebido', secretaria: null, criadoEm: agora.toISOString(),
      prazoEm: null, atrasado: false, anexo: { nome: pedido.arquivo.name, bytes: bytes.byteLength, sha256: hash, tipo },
      historico: [{ em: agora.toISOString(), status: 'Recebido', descricao: 'Pedido recebido' }],
    };
    this.protocolos.set(numero, p);
    this.registrar('Table Storage · protocolos', `PartitionKey ${agora.getFullYear()}, RowKey ${numero.split('/')[1]}`, 'storage');
    this.registrar('Service Bus · fila triagem', `MessageId ${numero}`, 'fila');
    setTimeout(() => this.triar(numero), this.atrasoFila);
    return { ...p };
  }

  /** Function "Triagem" (gatilho da fila). Idempotente, como no C#. */
  triar(numero: string) {
    const p = this.protocolos.get(numero);
    if (!p || p.status !== 'Recebido') return;
    const { secretaria, prazoDias } = classificar(p.assunto, p.descricao);
    const agora = this.agora();
    p.status = 'Encaminhado';
    p.secretaria = secretaria;
    p.prazoEm = somarDiasUteis(agora, prazoDias).toISOString();
    p.historico.push({ em: agora.toISOString(), status: 'Encaminhado', descricao: `Encaminhado para ${secretaria}` });
    this.registrar('Function · Triagem', `${numero} → ${secretaria} (prazo ${prazoDias} dias úteis)`, 'funcao');
    this.publicar(p, 'ENCAMINHADO');
  }

  andamento(numero: string, status: Status, descricao: string): Protocolo {
    const p = this.protocolos.get(numero);
    if (!p) throw new Error('Protocolo não encontrado');
    if (!podeIr(p.status, status)) throw new Error(`Não é possível passar de ${p.status} para ${status}`);
    p.status = status;
    p.historico.push({ em: this.agora().toISOString(), status, descricao });
    this.registrar('HTTP Function · RegistrarAndamento', `${numero} → ${status}`, 'http');
    this.publicar(p, status.toUpperCase());
    return { ...p };
  }

  private publicar(p: Protocolo, tipo: string) {
    this.registrar('Service Bus · tópico protocolos', `${tipo} → assinaturas "${p.secretaria?.toLowerCase()}" e "notificacoes"`, 'topico');
    setTimeout(() => this.registrar('Function · NotificarCidadao', `E-mail para ${p.cidadaoEmail[0]}***${p.cidadaoEmail.slice(p.cidadaoEmail.indexOf('@'))}`, 'funcao'),
      this.atrasoFila / 2);
  }

  private semear() {
    const exemplos: [string, string, number, Status][] = [
      ['Buraco na Rua das Acácias', 'Buraco grande em frente ao número 200, risco para motos.', 12, 'EmAnalise'],
      ['Segunda via do IPTU 2026', 'Não recebi o carnê do IPTU deste ano.', 9, 'Concluido'],
      ['Vaga em creche', 'Solicito vaga em creche para criança de 2 anos no bairro Jardim.', 4, 'Encaminhado'],
      ['Falta de vacina na UBS Centro', 'A vacina da gripe acabou na UBS Centro.', 8, 'Encaminhado'],
      ['Poda de árvore', 'Galhos encostando na fiação elétrica.', 20, 'Encaminhado'],
      ['Atualização do CadÚnico', 'Mudança de endereço e composição familiar.', 2, 'Concluido'],
    ];
    const hoje = this.agora();
    for (const [assunto, descricao, diasAtras, status] of exemplos) {
      const criado = new Date(hoje.getTime() - diasAtras * 86_400_000);
      const numero = formatarNumero(hoje.getFullYear(), ++this.sequencia);
      const { secretaria, prazoDias } = classificar(assunto, descricao);
      const prazo = somarDiasUteis(criado, prazoDias);
      const historico: Andamento[] = [
        { em: criado.toISOString(), status: 'Recebido', descricao: 'Pedido recebido' },
        { em: new Date(criado.getTime() + 60_000).toISOString(), status: 'Encaminhado', descricao: `Encaminhado para ${secretaria}` },
      ];
      if (status === 'EmAnalise' || status === 'Concluido') {
        historico.push({ em: new Date(criado.getTime() + 86_400_000).toISOString(), status, descricao: status === 'Concluido' ? 'Atendido' : 'Vistoria agendada' });
      }
      this.protocolos.set(numero, { numero, assunto, descricao, cidadaoNome: 'Cidadão de exemplo', cidadaoEmail: 'cidadao@exemplo.com',
        status, secretaria, criadoEm: criado.toISOString(), prazoEm: prazo.toISOString(),
        atrasado: status !== 'Concluido' && prazo < hoje,
        anexo: { nome: 'documento.pdf', bytes: 182_331, sha256: '9f2c'.padEnd(64, '0'), tipo: 'application/pdf' }, historico });
    }
  }
}
