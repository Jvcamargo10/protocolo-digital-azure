import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { ProtocoloDemo, type Protocolo } from './demo';
import { SECRETARIAS, proximosStatus, type Secretaria, type Status } from './regras';
import { ContextoIdioma, TEXTOS, data, dataHora, useIdioma, type Idioma } from './textos';

type Aba = 'abrir' | 'acompanhar' | 'secretarias' | 'arquitetura';

/** Faz o componente renderizar de novo quando a simulação avisa que algo mudou (fila processada etc.). */
function useVersao(demo: ProtocoloDemo) {
  const [versao, setVersao] = useState(0);
  useEffect(() => demo.aoMudar(() => setVersao((v) => v + 1)), [demo]);
  return versao;
}

function pdfDeExemplo(): File {
  const conteudo = '%PDF-1.4\n1 0 obj <</Type /Catalog>> endobj\ntrailer <</Root 1 0 R>>\n%%EOF\n';
  return new File([conteudo], 'comprovante-de-endereco.pdf', { type: 'application/pdf' });
}

function Etiqueta({ status }: { status: Status }) {
  const { t } = useIdioma();
  return <span className="status" data-status={status}>{t.status[status]}</span>;
}

function Detalhe({ p }: { p: Protocolo }) {
  const { idioma, t } = useIdioma();
  return (
    <article className="cartao detalhe">
      <div className="detalhe-topo">
        <span className="numero">{p.numero}</span>
        <Etiqueta status={p.status} />
        {p.atrasado && <span className="status" data-status="Atrasado">{t.atrasado}</span>}
      </div>
      <h3>{p.assunto}</h3>
      <p className="suave">{p.descricao}</p>
      <dl className="dados">
        <div><dt>{t.secretaria}</dt><dd>{p.secretaria ? t.secretarias[p.secretaria] : '—'}</dd></div>
        <div><dt>{t.prazo}</dt><dd>{p.prazoEm ? data(p.prazoEm, idioma) : '—'}</dd></div>
      </dl>
      <p className="hash"><span>{t.integridade}</span><code>{p.anexo.sha256}</code></p>
      <h4>{t.historico}</h4>
      <ol className="linha-tempo">
        {p.historico.map((h, i) => (
          <li key={i}><Etiqueta status={h.status} /> <span>{h.descricao}</span> <small>{dataHora(h.em, idioma)}</small></li>
        ))}
      </ol>
    </article>
  );
}

function Monitor({ demo }: { demo: ProtocoloDemo }) {
  const { idioma, t } = useIdioma();
  useVersao(demo);
  const passos = demo.monitor();
  return (
    <aside className="monitor" aria-live="polite">
      <h2>{t.monitor}</h2>
      {passos.length === 0 && <p className="suave">{t.monitorVazio}</p>}
      <ol>
        {passos.map((p, i) => (
          <li key={passos.length - i} data-tipo={p.tipo}>
            <strong>{p.recurso}</strong><span>{p.detalhe}</span><small>{dataHora(p.em, idioma)}</small>
          </li>
        ))}
      </ol>
    </aside>
  );
}

function Abrir({ demo, aoCriar }: { demo: ProtocoloDemo; aoCriar: (numero: string) => void }) {
  const { t } = useIdioma();
  const [form, setForm] = useState({ assunto: '', descricao: '', nome: '', email: '' });
  const [arquivo, setArquivo] = useState<File | null>(null);
  const [erro, setErro] = useState('');
  const [aviso, setAviso] = useState('');
  const [enviando, setEnviando] = useState(false);

  async function enviar(e: FormEvent) {
    e.preventDefault();
    if (!arquivo) { setErro(t.anexo); return; }
    setEnviando(true); setErro(''); setAviso('');
    try {
      const p = await demo.abrir({ ...form, arquivo });
      setAviso(t.recebido(p.numero));
      aoCriar(p.numero);
      setForm({ assunto: '', descricao: '', nome: form.nome, email: form.email });
      setArquivo(null);
    } catch (err) {
      setErro((err as Error).message);
    } finally {
      setEnviando(false);
    }
  }

  return (
    <form className="cartao formulario" onSubmit={enviar}>
      <label>{t.assunto}<input required value={form.assunto} maxLength={120} onChange={(e) => setForm({ ...form, assunto: e.target.value })} /></label>
      <p className="dica">{t.sugestoes}</p>
      <label>{t.descricao}<textarea required rows={4} maxLength={4000} value={form.descricao}
        onChange={(e) => setForm({ ...form, descricao: e.target.value })} /></label>
      <div className="linha">
        <label>{t.nome}<input required value={form.nome} onChange={(e) => setForm({ ...form, nome: e.target.value })} /></label>
        <label>{t.email}<input required type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} /></label>
      </div>
      <label>{t.anexo}<input type="file" accept=".pdf,.png,.jpg,.jpeg" onChange={(e) => setArquivo(e.target.files?.[0] ?? null)} /></label>
      <button type="button" className="link" onClick={() => setArquivo(pdfDeExemplo())}>{t.usarExemplo}</button>
      {arquivo && <p className="dica">📎 {arquivo.name} ({arquivo.size} bytes)</p>}
      {erro && <p className="erro" role="alert">{erro}</p>}
      {aviso && <p className="aviso" role="status">{aviso}</p>}
      <button disabled={enviando}>{enviando ? t.enviando : t.enviar}</button>
    </form>
  );
}

function Acompanhar({ demo, inicial }: { demo: ProtocoloDemo; inicial: string }) {
  const { t } = useIdioma();
  useVersao(demo);
  const [numero, setNumero] = useState(inicial);
  const [consulta, setConsulta] = useState(inicial);
  const p = consulta ? demo.obter(consulta) : null;
  return (
    <section>
      <form className="busca" onSubmit={(e) => { e.preventDefault(); setConsulta(numero); }}>
        <label>{t.numero}<input value={numero} placeholder="2026/000001" onChange={(e) => setNumero(e.target.value)} /></label>
        <button>{t.consultar}</button>
      </form>
      {consulta && !p && <p className="suave">{t.naoEncontrado}</p>}
      {p && <Detalhe p={p} />}
    </section>
  );
}

function Secretarias({ demo }: { demo: ProtocoloDemo }) {
  const { t } = useIdioma();
  useVersao(demo);
  const [secretaria, setSecretaria] = useState<Secretaria>('Obras');
  const [aberto, setAberto] = useState<string | null>(null);
  const [descricao, setDescricao] = useState('');
  const [erro, setErro] = useState('');
  const lista = demo.listar(secretaria);

  function agir(numero: string, status: Status) {
    try {
      demo.andamento(numero, status, descricao.trim() || (t.acoes[status] ?? status));
      setDescricao(''); setErro('');
    } catch (e) {
      setErro((e as Error).message);
    }
  }

  return (
    <section>
      <div className="seletor" role="tablist">
        {SECRETARIAS.map((s) => (
          <button key={s} role="tab" aria-selected={s === secretaria} onClick={() => setSecretaria(s)}>
            {t.secretarias[s]} <span className="contagem">{demo.listar(s).filter((p) => p.status !== 'Concluido' && p.status !== 'Indeferido').length}</span>
          </button>
        ))}
      </div>
      {erro && <p className="erro">{erro}</p>}
      {lista.length === 0 && <p className="suave">{t.semProtocolos}</p>}
      <ul className="lista">
        {lista.map((p) => (
          <li key={p.numero} className="cartao">
            <button className="linha-protocolo" onClick={() => setAberto(aberto === p.numero ? null : p.numero)}>
              <span className="numero">{p.numero}</span><span className="assunto">{p.assunto}</span>
              <Etiqueta status={p.status} />{p.atrasado && <span className="status" data-status="Atrasado">{t.atrasado}</span>}
            </button>
            {aberto === p.numero && (
              <div className="acoes-protocolo">
                <Detalhe p={p} />
                {proximosStatus(p.status).length > 0 && (
                  <div className="acoes">
                    <input placeholder={t.descricaoAndamento} value={descricao} onChange={(e) => setDescricao(e.target.value)} />
                    {proximosStatus(p.status).map((s) => (
                      <button key={s} className={s === 'Indeferido' ? 'secundario' : ''} onClick={() => agir(p.numero, s)}>{t.acoes[s]}</button>
                    ))}
                  </div>
                )}
              </div>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

const ARQUITETURA = {
  pt: [
    ['Azure Functions (.NET 10, isolated)', 'HTTP para abrir e consultar, gatilho de fila para a triagem, gatilho de tópico para notificar e timer diário para prazos.'],
    ['Service Bus', 'Fila "triagem" com detecção de duplicidade (MessageId = número) e dead-letter após 5 falhas. Tópico "protocolos" com uma assinatura filtrada por secretaria.'],
    ['Table Storage', 'Protocolo lido por chave (ano + sequência). Número anual gerado com concorrência otimista (ETag), sem repetir mesmo com várias instâncias.'],
    ['Blob Storage', 'Anexos em container privado, validados pelo conteúdo, com SHA-256 guardado como prova de integridade e soft delete de 30 dias.'],
    ['Segurança', 'Sem connection strings em produção: Managed Identity com RBAC de privilégio mínimo; chaves de acesso do Storage e do Service Bus desativadas.'],
    ['Bicep + Application Insights', 'Toda a infraestrutura como código, com Log Analytics e alertas para mensagens mortas e falhas HTTP.'],
  ],
  en: [
    ['Azure Functions (.NET 10, isolated)', 'HTTP to submit and track, queue trigger for routing, topic trigger for notifications and a daily timer for deadlines.'],
    ['Service Bus', '"triagem" queue with duplicate detection (MessageId = number) and dead-lettering after 5 failures. "protocolos" topic with one filtered subscription per department.'],
    ['Table Storage', 'Point reads by key (year + sequence). Yearly number generated with optimistic concurrency (ETag), never repeated across instances.'],
    ['Blob Storage', 'Attachments in a private container, validated by content, with SHA-256 stored as integrity proof and 30-day soft delete.'],
    ['Security', 'No connection strings in production: Managed Identity with least-privilege RBAC; Storage and Service Bus access keys disabled.'],
    ['Bicep + Application Insights', 'All infrastructure as code, with Log Analytics and alerts for dead-lettered messages and HTTP failures.'],
  ],
};

export function App({ demo: injetada }: { demo?: ProtocoloDemo }) {
  const demo = useMemo(() => injetada ?? new ProtocoloDemo(), [injetada]);
  const [idioma, setIdioma] = useState<Idioma>(() => {
    try { const s = localStorage.getItem('protocolo-idioma'); if (s === 'pt' || s === 'en') return s; } catch { /* ignora */ }
    return navigator.language.startsWith('pt') ? 'pt' : 'en';
  });
  const [aba, setAba] = useState<Aba>('abrir');
  const [ultimo, setUltimo] = useState('');
  const t = TEXTOS[idioma];

  function trocar(i: Idioma) {
    setIdioma(i);
    document.documentElement.lang = i === 'pt' ? 'pt-BR' : 'en';
    try { localStorage.setItem('protocolo-idioma', i); } catch { /* ignora */ }
  }

  return (
    <ContextoIdioma.Provider value={{ idioma, t }}>
      <header className="cabecalho">
        <div className="marca">
          <svg viewBox="0 0 32 32" aria-hidden="true"><rect x="6" y="3" width="20" height="26" rx="3" /><path d="M11 11h10M11 16h10M11 21h6" /></svg>
          <div><h1>{t.titulo}</h1><p>{t.subtitulo}</p></div>
        </div>
        <div className="idiomas" role="group" aria-label="Idioma / Language">
          {(['pt', 'en'] as Idioma[]).map((i) => <button key={i} aria-pressed={idioma === i} onClick={() => trocar(i)}>{i.toUpperCase()}</button>)}
        </div>
      </header>
      <p className="faixa-demo">{t.demo}</p>
      <nav className="abas" role="tablist">
        {(Object.keys(t.abas) as Aba[]).map((a) => (
          <button key={a} role="tab" aria-selected={aba === a} onClick={() => setAba(a)}>{t.abas[a]}</button>
        ))}
      </nav>
      <main>
        {aba === 'abrir' && (
          <div className="duas-colunas">
            <Abrir demo={demo} aoCriar={setUltimo} />
            <Monitor demo={demo} />
          </div>
        )}
        {aba === 'acompanhar' && <Acompanhar demo={demo} inicial={ultimo || '2026/000001'} key={ultimo} />}
        {aba === 'secretarias' && <Secretarias demo={demo} />}
        {aba === 'arquitetura' && (
          <div className="blocos">
            {ARQUITETURA[idioma].map(([titulo, texto]) => <section key={titulo} className="cartao"><h3>{titulo}</h3><p>{texto}</p></section>)}
          </div>
        )}
      </main>
      <footer><a href="https://github.com/Jvcamargo10/protocolo-digital-azure">GitHub</a> · .NET · Azure Functions · Service Bus · Storage · Bicep</footer>
    </ContextoIdioma.Provider>
  );
}
