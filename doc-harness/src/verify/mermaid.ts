import { existsSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import type { HarnessConfig } from '../config.js';
import { listSourceFiles } from '../fsx.js';
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
const EXTERNAL_ACTORS = /^(Client|Browser|User|Admin|Author|Visitor|Operator|Maintainer|Developer|Crawler|Bot|Internet|OS|FileSystem|Disk|Network|Shell|Terminal|Host|PostgreSQL|Postgres|Caddy|Docker|Compose|GitHub|GitHubActions|CI|Cron|Scheduler|Timer|SMTP|DNS|TLS|HTTP|HTTPS|ACME|AcmeCa)$/i;
/** 의미 없는 자동 id(A1, B23). 기능 id(F001)는 제외한다. */
const MEANINGLESS_ID_RE = /^(?!F\d{3}$)[A-Z]{1,2}\d{1,3}$/;
/** 개념 노드가 자연스러운 다이어그램: 노드 이름이 코드에 그대로 있을 필요가 없다. */
const CONCEPTUAL_TYPES = new Set(['flowchart', 'graph', 'stateDiagram-v2', 'stateDiagram', 'erDiagram']);

export interface MermaidStats { nodes: Set<string>; edges: number; type: string; aliases: Map<string, string> }

/** 정규식으로 노드·간선 수를 센다(정밀할 필요 없음, 복잡도 상한과 이름 검사용). */
export function mermaidStats(code: string): MermaidStats {
  const lines = code.split('\n').map((l) => l.trim()).filter((l) => l && !l.startsWith('%%'));
  const type = (lines[0] ?? '').split(/\s+/)[0] || 'unknown';
  const nodes = new Set<string>();
  const aliases = new Map<string, string>();
  let edges = 0;
  const addId = (raw: string) => {
    const id = raw.trim().replace(/^["']|["']$/g, '');
    const m = /^([A-Za-z_][\w.-]*)/.exec(id);
    if (m && !['end', 'subgraph', 'participant', 'actor', 'direction', 'flowchart', 'graph', 'sequenceDiagram', 'stateDiagram-v2', 'erDiagram', 'classDiagram', 'Note', 'note', 'loop', 'alt', 'else', 'opt', 'par', 'and', 'rect', 'activate', 'deactivate', 'class', 'state'].includes(m[1])) nodes.add(m[1]);
  };
  for (const line of lines.slice(1)) {
    const p = /^(?:participant|actor)\s+([\w.-]+)(?:\s+as\s+(.+))?/.exec(line);
    if (p) { nodes.add(p[1]); if (p[2]) aliases.set(p[1], p[2].trim().replace(/^["']|["']$/g, '')); continue; }
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
  return { nodes, edges, type, aliases };
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

const DIRECTORY = Symbol('directory');
const fileTextCache = new Map<string, string | null | typeof DIRECTORY>();
let repoFilesCache: { root: string; files: string[] } | null = null;

/** 짧은 표기(`Caddyfile`, `Posts/PostEndpoints.cs`)를 저장소 파일 목록의 접미 일치로 실제 경로에 대응시킨다. */
function resolveRel(projectRoot: string, rel: string, cfg: HarnessConfig): string {
  if (existsSync(path.join(projectRoot, rel))) return rel;
  if (!repoFilesCache || repoFilesCache.root !== projectRoot) repoFilesCache = { root: projectRoot, files: listSourceFiles(projectRoot, cfg) };
  const suffix = '/' + rel.replace(/^\.\//, '');
  return repoFilesCache.files.find((f) => f.endsWith(suffix)) ?? rel;
}

/** 파일이면 본문, 디렉터리면 DIRECTORY(아키텍처 노드는 디렉터리를 가리킬 수 있다), 없으면 null. */
function fileText(projectRoot: string, rel: string, cfg: HarnessConfig): string | null | typeof DIRECTORY {
  if (!fileTextCache.has(rel)) {
    const abs = path.join(projectRoot, resolveRel(projectRoot, rel, cfg));
    if (!existsSync(abs)) fileTextCache.set(rel, null);
    else if (statSync(abs).isDirectory()) fileTextCache.set(rel, DIRECTORY);
    else fileTextCache.set(rel, readFileSync(abs, 'utf8'));
  }
  return fileTextCache.get(rel)!;
}

/** 코드 근거가 "없음"을 뜻하는 표기(UNKNOWN, -, n/a). 파일 부재로 차단하지 않고 경고로 낸다. */
const NO_EVIDENCE_RE = /^(unknown|-|n\/a|none|없음)$/i;

const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9]/g, '');

/**
 * 노드 이름이 근거 파일과 "느슨하게" 대응하는가: 이름 전체, 점(.)으로 나눈 조각, 파일 basename(확장자 제거)과의 대소문자·기호 무시 비교.
 * `SessionRules.IsValid`→`SessionRules`, `queryClient`→`queryClient.ts`, `BackupSh`→`backup.sh`가 통과한다.
 */
function nodeMatchesFile(node: string, code: string, text: string): boolean {
  const token = node.split(/[\s(]/)[0];
  if (token.length <= 2) return true;
  const segments = token.split('.').filter((s) => s.length > 2);
  if ([token, ...segments].some((s) => text.includes(s))) return true;
  const base = path.posix.basename(code).replace(/\.[^.]+$/, '');
  return norm(base) === norm(token) || norm(base).includes(norm(token)) || norm(token).includes(norm(base));
}

export interface MermaidCheckResult {
  /** 차단·수정 대상. */
  issues: DiagramIssue[];
  /** 보고만 하는 경고(결정적 검사가 확신할 수 없는 이름 대조·participant 대조). */
  warnings: DiagramIssue[];
  parser: 'VERIFIED' | 'MERMAID_RENDER_NOT_VERIFIED';
}

/**
 * Mermaid 검증 3층(스펙 §8): 구조(펜스·앵커) → 문법(mermaid.parse) → 규칙 린트(스타일·이름·복잡도) + 코드 대조.
 * 코드 대조 중 "노드 이름 ↔ 파일 본문", "participant ⊆ 실행 흐름"은 개념 노드·별칭 때문에 오탐이 잦아 경고로만 낸다(2026-09-24 실측 254건 오탐).
 */
export async function checkMermaid(docs: Map<string, string>, ws: CurrentWorkspace, cfg: HarnessConfig, projectRoot: string): Promise<MermaidCheckResult> {
  const parser = await loadMermaidParser();
  const issues: DiagramIssue[] = [];
  const warnings: DiagramIssue[] = [];
  fileTextCache.clear();
  for (const [name, md] of docs) {
    const parsed = parseSections(md);
    for (const i of parsed.issues) issues.push({ document: name, diagram: '(문서)', type: i.type, description: `${i.detail} (line ${i.line})`, evidence: [] });
    const sectionById = new Map(parsed.sections.map((s) => [s.id, s]));
    const featureId = /^features\/(F\d{3})_/.exec(name)?.[1];
    const fa = featureId ? ws.featureAnalyses.get(featureId) : undefined;
    for (const block of extractMermaidBlocks(md)) {
      const diagram = block.sectionId ?? `(line ${block.line})`;
      const push = (list: DiagramIssue[], type: DiagramIssue['type'], description: string, evidence: DiagramIssue['evidence'] = []) => list.push({ document: name, section: block.sectionId, diagram, type, description, evidence });
      if (parser) {
        const r = await parser(block.code);
        if (!r.ok) push(issues, 'MERMAID_SYNTAX_ERROR', r.error);
      }
      if (FORBIDDEN_RE.test(block.code)) push(issues, 'MERMAID_STYLE_FORBIDDEN', 'style/classDef/linkStyle/%%{init/HTML 라벨은 금지다');
      const stats = mermaidStats(block.code);
      const meaningless = [...stats.nodes].filter((n) => MEANINGLESS_ID_RE.test(n));
      if (meaningless.length >= 2) push(issues, 'DIAGRAM_ABSTRACT_NODE', `의미 없는 노드 id: ${meaningless.slice(0, 6).join(', ')}`);
      const abstract = [...stats.nodes].filter((n) => ABSTRACT.has(n));
      if (abstract.length) push(issues, 'DIAGRAM_ABSTRACT_NODE', `추상 이름만 있는 노드(실제 클래스명 필요): ${abstract.join(', ')}`);
      if (stats.nodes.size > cfg.diagrams.max_nodes || stats.edges > cfg.diagrams.max_edges) push(issues, 'DIAGRAM_TOO_COMPLEX', `노드 ${stats.nodes.size}/${cfg.diagrams.max_nodes}, 간선 ${stats.edges}/${cfg.diagrams.max_edges} — Level을 나눈다`);
      // 코드 근거 표 ↔ mermaid ↔ 실제 파일
      const section = block.sectionId ? sectionById.get(block.sectionId) : undefined;
      if (section) {
        const rows = nodeTableRows(section.body);
        for (const row of rows) {
          if (NO_EVIDENCE_RE.test(row.code.trim())) { push(warnings, 'DIAGRAM_NODE_NOT_IN_CODE', `코드 근거가 비어 있다: ${row.node} → ${row.code}`); continue; }
          const text = fileText(projectRoot, row.code, cfg);
          if (text === null) { push(issues, 'DIAGRAM_NODE_NOT_IN_CODE', `코드 근거 파일이 없다: ${row.node} → ${row.code}`, [{ file: row.code }]); continue; }
          // 코드 근거 표에 mermaid에 없는 행이 있는 것은 추가 참조일 뿐 오류가 아니다 → 경고(2026-09-25 실측: 수정 회차에서 16건이 발행을 막음).
          if (!block.code.includes(row.node.split(/[\s(]/)[0])) push(warnings, 'DIAGRAM_MISSING', `코드 근거 표의 노드가 mermaid 본문에 없다: ${row.node}`);
          if (text === DIRECTORY || CONCEPTUAL_TYPES.has(stats.type)) continue;
          const token = row.node.split(/[\s(]/)[0];
          if (!EXTERNAL_ACTORS.test(token) && !nodeMatchesFile(row.node, row.code, text)) push(warnings, 'DIAGRAM_NODE_NOT_IN_CODE', `노드 이름이 근거 파일에 등장하지 않는다: ${row.node} (${row.code})`, [{ file: row.code }]);
        }
        if (fa && stats.type === 'sequenceDiagram') {
          const known = [...fa.executionFlow.map((s) => s.component), ...fa.relatedCode.map((c) => c.symbol ?? ''), ...fa.relatedCode.map((c) => path.posix.basename(c.file).replace(/\.\w+$/, '')), ...rows.map((r) => r.node), ...stats.aliases.values()].map(norm).filter(Boolean);
          for (const p of stats.nodes) {
            if (EXTERNAL_ACTORS.test(p) || /^[a-z]/.test(p)) continue; // 소문자 시작은 개념·별칭으로 본다
            const np = norm(p);
            if (known.some((k) => k.includes(np) || np.includes(k))) continue;
            push(warnings, 'INCORRECT_DIAGRAM_RELATION', `participant ${p}가 기능 ${fa.feature.id}의 실행 흐름·관련 코드에 없다`);
          }
        }
      }
    }
  }
  return { issues, warnings, parser: parser ? 'VERIFIED' : 'MERMAID_RENDER_NOT_VERIFIED' };
}
