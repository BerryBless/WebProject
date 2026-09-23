import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import type { HarnessConfig } from '../config.js';
import { extractMermaidBlocks, parseSections } from '../render/sections.js';
import type { CurrentWorkspace, DiagramIssue } from '../types.js';

export type MermaidParser = (code: string) => Promise<{ ok: true; type: string } | { ok: false; error: string }>;

let parserPromise: Promise<MermaidParser | null> | null = null;

/** mermaid 12 + jsdom을 브라우저 없이 로드한다(스파이크 S4). 실패하면 null → MERMAID_RENDER_NOT_VERIFIED. */
export function loadMermaidParser(): Promise<MermaidParser | null> {
  parserPromise ??= (async () => {
    try {
      const { JSDOM } = await import('jsdom');
      const dom = new JSDOM('<!DOCTYPE html><body></body>');
      const g = globalThis as Record<string, unknown>;
      g.window ??= dom.window;
      g.document ??= dom.window.document;
      g.DOMPurify ??= undefined;
      const mermaid = (await import('mermaid')).default;
      return async (code: string) => {
        try {
          const r = await mermaid.parse(code, { suppressErrors: false });
          return { ok: true as const, type: (r && typeof r === 'object' && 'diagramType' in r ? String((r as { diagramType: string }).diagramType) : 'unknown') };
        } catch (e) {
          return { ok: false as const, error: String((e as Error).message ?? e).split('\n').slice(0, 2).join(' ').slice(0, 300) };
        }
      };
    } catch {
      return null;
    }
  })();
  return parserPromise;
}

const FORBIDDEN_RE = /^\s*(style\s|classDef\s|linkStyle\s|%%\{init)|<br\s*\/?>|<b>|<i>|<span/m;
const ABSTRACT = new Set(['Controller', 'Service', 'Repository', 'Repo', 'DB', 'Database', 'Worker', 'Handler', 'Manager', 'Helper', 'Util', 'Utils', 'Cache', 'Queue', 'Server', 'Backend', 'Frontend']);
const EXTERNAL_ACTORS = /^(Client|Browser|User|Admin|Author|Visitor|Crawler|Bot|Internet|OS|FileSystem|Disk|Network|PostgreSQL|Postgres|Caddy|Docker|Compose|GitHub|GitHubActions|CI|Cron|Scheduler|Timer|SMTP|DNS|TLS|HTTP|HTTPS)$/i;
const MEANINGLESS_ID_RE = /^[A-Z]{1,2}\d{1,3}$/;

export interface MermaidStats { nodes: Set<string>; edges: number; type: string }

/** 정규식으로 노드·간선 수를 센다(정밀할 필요 없음, 복잡도 상한과 이름 검사용). */
export function mermaidStats(code: string): MermaidStats {
  const lines = code.split('\n').map((l) => l.trim()).filter((l) => l && !l.startsWith('%%'));
  const type = (lines[0] ?? '').split(/\s+/)[0] || 'unknown';
  const nodes = new Set<string>();
  let edges = 0;
  const addId = (raw: string) => {
    const id = raw.trim().replace(/^["']|["']$/g, '');
    const m = /^([A-Za-z_][\w.-]*)/.exec(id);
    if (m && !['end', 'subgraph', 'participant', 'actor', 'direction', 'flowchart', 'graph', 'sequenceDiagram', 'stateDiagram-v2', 'erDiagram', 'classDiagram', 'Note', 'note', 'loop', 'alt', 'else', 'opt', 'par', 'and', 'rect', 'activate', 'deactivate', 'class', 'state'].includes(m[1])) nodes.add(m[1]);
  };
  for (const line of lines.slice(1)) {
    const p = /^(?:participant|actor)\s+([\w.-]+)/.exec(line);
    if (p) { nodes.add(p[1]); continue; }
    const parts = line.split(/\s*(?:-->>|-->|->>|-\)|--\)|-x|--x|-\.->|==>|-\.-|---|--|\|\|--o\{|\|\|--\|\||\}o--\|\||\}o--o\{|\|o--o\||<\|--|\*--|o--|<--|\.\.>|\.\.\|>|->)\s*/);
    if (parts.length >= 2) {
      edges += parts.length - 1;
      for (const part of parts) {
        const side = part.replace(/^\|[^|]*\|\s*/, '').split(/[:|]/)[0];
        addId(side.replace(/\[.*$|\(.*$|\{.*$/, ''));
      }
    } else {
      const decl = /^([A-Za-z_][\w.-]*)\s*[\[\(\{]/.exec(line);
      if (decl) addId(decl[1]);
    }
  }
  return { nodes, edges, type };
}

function nodeTableRows(body: string): { node: string; code: string }[] {
  const rows: { node: string; code: string }[] = [];
  const tableStart = body.indexOf('코드 근거');
  if (tableStart < 0) return rows;
  for (const line of body.slice(tableStart).split('\n')) {
    const m = /^\|\s*([^|]+?)\s*\|\s*`([^`]+)`/.exec(line);
    if (m && m[1] !== '구성 요소' && !/^-+$/.test(m[1])) rows.push({ node: m[1].trim(), code: m[2].trim() });
  }
  return rows;
}

const fileTextCache = new Map<string, string | null>();
function fileText(projectRoot: string, rel: string): string | null {
  if (!fileTextCache.has(rel)) {
    const abs = path.join(projectRoot, rel);
    fileTextCache.set(rel, existsSync(abs) ? readFileSync(abs, 'utf8') : null);
  }
  return fileTextCache.get(rel)!;
}

/**
 * Mermaid 검증 3층(스펙 §8): 구조(펜스·앵커) → 문법(mermaid.parse) → 규칙 린트(스타일·이름·복잡도) + 코드 대조(노드↔파일, participant ⊆ executionFlow).
 */
export async function checkMermaid(docs: Map<string, string>, ws: CurrentWorkspace, cfg: HarnessConfig, projectRoot: string): Promise<{ issues: DiagramIssue[]; parser: 'VERIFIED' | 'MERMAID_RENDER_NOT_VERIFIED' }> {
  const parser = await loadMermaidParser();
  const issues: DiagramIssue[] = [];
  fileTextCache.clear();
  for (const [name, md] of docs) {
    const parsed = parseSections(md);
    for (const i of parsed.issues) issues.push({ document: name, diagram: '(문서)', type: i.type, description: `${i.detail} (line ${i.line})`, evidence: [] });
    const sectionById = new Map(parsed.sections.map((s) => [s.id, s]));
    const featureId = /^features\/(F\d{3})_/.exec(name)?.[1];
    const fa = featureId ? ws.featureAnalyses.get(featureId) : undefined;
    for (const block of extractMermaidBlocks(md)) {
      const diagram = block.sectionId ?? `(line ${block.line})`;
      const push = (type: DiagramIssue['type'], description: string, evidence: DiagramIssue['evidence'] = []) => issues.push({ document: name, section: block.sectionId, diagram, type, description, evidence });
      if (parser) {
        const r = await parser(block.code);
        if (!r.ok) push('MERMAID_SYNTAX_ERROR', r.error);
      }
      if (FORBIDDEN_RE.test(block.code)) push('MERMAID_STYLE_FORBIDDEN', 'style/classDef/linkStyle/%%{init/HTML 라벨은 금지다');
      const stats = mermaidStats(block.code);
      const meaningless = [...stats.nodes].filter((n) => MEANINGLESS_ID_RE.test(n));
      if (meaningless.length >= 2) push('DIAGRAM_ABSTRACT_NODE', `의미 없는 노드 id: ${meaningless.slice(0, 6).join(', ')}`);
      const abstract = [...stats.nodes].filter((n) => ABSTRACT.has(n));
      if (abstract.length) push('DIAGRAM_ABSTRACT_NODE', `추상 이름만 있는 노드(실제 클래스명 필요): ${abstract.join(', ')}`);
      if (stats.nodes.size > cfg.diagrams.max_nodes || stats.edges > cfg.diagrams.max_edges) push('DIAGRAM_TOO_COMPLEX', `노드 ${stats.nodes.size}/${cfg.diagrams.max_nodes}, 간선 ${stats.edges}/${cfg.diagrams.max_edges} — Level을 나눈다`);
      // 코드 근거 표 ↔ mermaid ↔ 실제 파일
      const section = block.sectionId ? sectionById.get(block.sectionId) : undefined;
      if (section) {
        for (const row of nodeTableRows(section.body)) {
          const text = fileText(projectRoot, row.code);
          if (text === null) { push('DIAGRAM_NODE_NOT_IN_CODE', `코드 근거 파일이 없다: ${row.node} → ${row.code}`, [{ file: row.code }]); continue; }
          if (!block.code.includes(row.node)) push('DIAGRAM_MISSING', `코드 근거 표의 노드가 mermaid 본문에 없다: ${row.node}`);
          const token = row.node.split(/[\s(]/)[0];
          if (!EXTERNAL_ACTORS.test(token) && token.length > 2 && !text.includes(token)) push('DIAGRAM_NODE_NOT_IN_CODE', `노드 이름이 근거 파일에 등장하지 않는다: ${row.node} (${row.code})`, [{ file: row.code }]);
        }
        if (fa && stats.type === 'sequenceDiagram') {
          const known = new Set<string>([...fa.executionFlow.map((s) => s.component), ...fa.relatedCode.map((c) => c.symbol ?? ''), ...fa.relatedCode.map((c) => path.posix.basename(c.file).replace(/\.\w+$/, '')), ...nodeTableRows(section.body).map((r) => r.node)]);
          for (const p of stats.nodes) {
            if (EXTERNAL_ACTORS.test(p) || known.has(p) || [...known].some((k) => k.includes(p) || p.includes(k))) continue;
            push('INCORRECT_DIAGRAM_RELATION', `participant ${p}가 기능 ${fa.feature.id}의 실행 흐름·관련 코드에 없다`);
          }
        }
      }
    }
  }
  return { issues, parser: parser ? 'VERIFIED' : 'MERMAID_RENDER_NOT_VERIFIED' };
}
