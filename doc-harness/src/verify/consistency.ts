import type { TruthLists } from '../context.js';
import type { CurrentWorkspace, Issue } from '../types.js';

/** 표 행의 메서드 칸은 `GET`뿐 아니라 `GET, HEAD`·`GET/HEAD`처럼 여러 개일 수 있다(2026-09-25 실측: 단일 메서드만 받아 10건 오탐). */
const EP_ROW_RE = /^\|\s*([A-Z]+(?:\s*[,\/|]\s*[A-Z]+)*)\s*\|\s*`([^`]+)`/gm;
const EP_TEXT_RE = /\b(GET|POST|PUT|DELETE|PATCH|HEAD)\s+(\/[\w\-{}:./*]*)/g;

function methodsOf(m: string): string[] {
  return m.split(/[,\/\s]+/).map((x) => x.trim().toUpperCase()).filter((x) => /^(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)$/.test(x));
}

function epKey(method: string, p: string): string {
  return `${method.toUpperCase()} ${p.replace(/\/$/, '') || '/'}`;
}

/** "GET, HEAD /x" 같은 복합 메서드를 메서드별 키로 편다. */
function epKeys(method: string, p: string): string[] {
  const ms = methodsOf(method);
  return (ms.length ? ms : [method]).map((m) => epKey(m, p));
}

export interface ConsistencyResult { issues: Issue[]; warnings: Issue[] }

/**
 * 교차 일관성 색인(스펙 §8.2): 08_API = api.json, 07_DATA_MODEL = data.json, 02_ARCHITECTURE ⊇ components(id·name·path 중 하나),
 * 코드 정답 목록(정규식) ⊆ api.json. 기능 문서의 엔드포인트 언급 누락과 HEAD 변형은 경고로만 낸다.
 */
export function checkConsistency(docs: Map<string, string>, ws: CurrentWorkspace, truth?: TruthLists): ConsistencyResult {
  const issues: Issue[] = [];
  const warnings: Issue[] = [];
  const apiKeys = new Set(ws.api.endpoints.flatMap((e) => epKeys(e.method, e.path)));

  const apiDoc = docs.get('08_API.md');
  if (apiDoc) {
    const docKeys = new Set([...apiDoc.matchAll(EP_ROW_RE)].flatMap((m) => epKeys(m[1], m[2])));
    for (const k of apiKeys) if (!docKeys.has(k)) issues.push({ document: '08_API.md', type: 'MISSING_ENDPOINT_IN_DOC', description: `api.json에 있는 엔드포인트가 표에 없다: ${k}`, evidence: [] });
    for (const k of docKeys) if (!apiKeys.has(k)) issues.push({ document: '08_API.md', type: 'EXTRA_ENDPOINT_IN_DOC', description: `표에 있으나 api.json에 없는 엔드포인트: ${k}`, evidence: [] });
  }

  const featureMentions = new Set<string>();
  for (const [name, md] of docs) {
    if (!name.startsWith('features/')) continue;
    for (const m of md.matchAll(EP_TEXT_RE)) featureMentions.add(epKey(m[1], m[2]));
  }
  if (featureMentions.size) {
    for (const e of ws.api.endpoints) {
      for (const k of epKeys(e.method, e.path)) {
        const asGet = epKey('GET', e.path);
        if (!featureMentions.has(k) && !(k.startsWith('HEAD ') && featureMentions.has(asGet)) && e.featureIds.length) {
          warnings.push({ document: '09_FEATURES.md', type: 'ENDPOINT_NOT_IN_FEATURE_DOCS', description: `엔드포인트 ${k}(기능 ${e.featureIds.join(',')})를 어떤 기능 문서도 언급하지 않는다`, evidence: [{ file: e.file }] });
        }
      }
    }
  }

  const dataDoc = docs.get('07_DATA_MODEL.md');
  if (dataDoc) {
    const headings = new Set([...dataDoc.matchAll(/^### ([\w.]+)/gm)].map((m) => m[1]));
    for (const e of ws.data.entities) if (!headings.has(e.name)) issues.push({ document: '07_DATA_MODEL.md', type: 'MISSING_ENTITY_IN_DOC', description: `data.json 엔티티가 문서에 없다: ${e.name}`, evidence: [{ file: e.file }] });
  }

  const archDoc = docs.get('02_ARCHITECTURE.md');
  if (archDoc) {
    for (const c of ws.architecture.components) {
      const mentioned = [c.id, c.name, c.path, ...c.name.split(/\s*[\/(]\s*/).map((s) => s.trim()).filter((s) => s.length > 3)].some((k) => k && archDoc.includes(k));
      if (!mentioned) warnings.push({ document: '02_ARCHITECTURE.md', type: 'MISSING_COMPONENT_IN_DOC', description: `architecture.json 컴포넌트가 문서에 없다: ${c.id} (${c.name})`, evidence: c.evidence });
    }
  }

  if (truth) {
    for (const t of truth.endpoints) {
      if (!t.path || t.path === '/') continue;
      const k = epKey(t.method, t.path);
      const matched = apiKeys.has(k) || [...apiKeys].some((a) => a.startsWith(t.method + ' ') && a.endsWith(t.path.replace(/\/$/, '')));
      if (!matched) issues.push({ document: '08_API.md', type: 'MISSING_ENDPOINT', description: `코드에 있는 엔드포인트가 api.json에 없다: ${k} (${t.file})`, evidence: [{ file: t.file }] });
    }
    for (const t of truth.entities) if (!ws.data.entities.some((e) => e.name === t.name)) issues.push({ document: '07_DATA_MODEL.md', type: 'MISSING_ENTITY', description: `코드의 DbSet 엔티티가 data.json에 없다: ${t.name} (${t.file})`, evidence: [{ file: t.file }] });
  }
  return { issues, warnings };
}
