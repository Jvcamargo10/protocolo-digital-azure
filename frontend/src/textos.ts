import { createContext, useContext } from 'react';
import type { Secretaria, Status } from './regras';

export type Idioma = 'pt' | 'en';

const pt = {
  titulo: 'Protocolo Digital',
  subtitulo: 'Pedidos à prefeitura de Vale Azul (fictícia), sem papel e sem fila',
  demo: 'Demonstração: roda no seu navegador, com dados fictícios. Nada é enviado a servidor algum.',
  abas: { abrir: 'Abrir pedido', acompanhar: 'Acompanhar', secretarias: 'Secretarias', arquitetura: 'Arquitetura' },
  assunto: 'Assunto', descricao: 'Descreva o pedido', nome: 'Nome completo', email: 'E-mail',
  anexo: 'Documento (PDF, PNG ou JPEG, até 10 MB)', usarExemplo: 'Usar um PDF de exemplo', enviar: 'Enviar pedido', enviando: 'Enviando…',
  sugestoes: 'Experimente: "Buraco na calçada", "Segunda via do IPTU", "Vaga em creche"',
  recebido: (n: string) => `Pedido ${n} recebido. Acompanhe a triagem automática no monitor ao lado.`,
  monitor: 'O que aconteceu no Azure', monitorVazio: 'Envie um pedido para ver o caminho dele pelas Functions, Storage e Service Bus.',
  numero: 'Número do protocolo', consultar: 'Consultar', naoEncontrado: 'Protocolo não encontrado.',
  secretaria: 'Secretaria', prazo: 'Prazo', atrasado: 'Prazo vencido', semProtocolos: 'Nenhum protocolo nesta secretaria.',
  integridade: 'Integridade do anexo (SHA-256)', historico: 'Andamentos',
  acoes: { EmAnalise: 'Iniciar análise', Concluido: 'Concluir', Indeferido: 'Indeferir' } as Partial<Record<Status, string>>,
  descricaoAndamento: 'Descrição do andamento',
  status: { Recebido: 'Recebido', Encaminhado: 'Encaminhado', EmAnalise: 'Em análise', Concluido: 'Concluído', Indeferido: 'Indeferido' } as Record<Status, string>,
  secretarias: { Saude: 'Saúde', Educacao: 'Educação', Obras: 'Obras', Fazenda: 'Fazenda', Assistencia: 'Assistência Social', Ouvidoria: 'Ouvidoria (triagem manual)' } as Record<Secretaria, string>,
};

type Textos = typeof pt;

const en: Textos = {
  titulo: 'Digital Protocol',
  subtitulo: 'Requests to the city of Vale Azul (fictitious), paperless and queue-free',
  demo: 'Demo: runs in your browser with fictitious data. Nothing is sent to any server.',
  abas: { abrir: 'New request', acompanhar: 'Track', secretarias: 'Departments', arquitetura: 'Architecture' },
  assunto: 'Subject', descricao: 'Describe your request', nome: 'Full name', email: 'E-mail',
  anexo: 'Document (PDF, PNG or JPEG, up to 10 MB)', usarExemplo: 'Use a sample PDF', enviar: 'Submit request', enviando: 'Sending…',
  sugestoes: 'Routing rules read Portuguese. Try: "Buraco na calçada", "Segunda via do IPTU", "Vaga em creche"',
  recebido: (n: string) => `Request ${n} received. Watch the automatic routing in the monitor.`,
  monitor: 'What happened in Azure', monitorVazio: 'Submit a request to see its path through Functions, Storage and Service Bus.',
  numero: 'Protocol number', consultar: 'Look up', naoEncontrado: 'Protocol not found.',
  secretaria: 'Department', prazo: 'Due', atrasado: 'Overdue', semProtocolos: 'No protocols for this department.',
  integridade: 'Attachment integrity (SHA-256)', historico: 'History',
  acoes: { EmAnalise: 'Start review', Concluido: 'Complete', Indeferido: 'Reject' },
  descricaoAndamento: 'Update description',
  status: { Recebido: 'Received', Encaminhado: 'Routed', EmAnalise: 'Under review', Concluido: 'Completed', Indeferido: 'Rejected' },
  secretarias: { Saude: 'Health', Educacao: 'Education', Obras: 'Public Works', Fazenda: 'Finance', Assistencia: 'Social Services', Ouvidoria: 'Ombudsman (manual routing)' },
};

export const TEXTOS: Record<Idioma, Textos> = { pt, en };
export const ContextoIdioma = createContext<{ idioma: Idioma; t: Textos }>({ idioma: 'pt', t: pt });
export const useIdioma = () => useContext(ContextoIdioma);

export const dataHora = (iso: string, idioma: Idioma) =>
  new Date(iso).toLocaleString(idioma === 'pt' ? 'pt-BR' : 'en-US', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' });
export const data = (iso: string, idioma: Idioma) =>
  new Date(iso).toLocaleDateString(idioma === 'pt' ? 'pt-BR' : 'en-US', { day: '2-digit', month: 'short', year: 'numeric' });
