import type { TruthLists } from '../context.js';
import type { CurrentWorkspace, Issue } from '../types.js';

const EP_ROW_RE = /^\|\s*(GET|POST|PUT|DELETE|PATCH|HEAD)\s*\|\s*`([^`]+)`/gm;
const EP_TEXT_RE = /\b(GET|POST|PUT|DELETE|PATCH)\s+(\/[\w\-{}:./*]*)/g;

function epKey(method: string, p: string): string {
  return `${method.toUpperCase()} ${p.replace(/\/$/, '') || '/'}`;
}

/**
 * 교차 일관성 색인(스펙 §8.2): 08_API = api.json = 기능 문서 합집합, 07_DATA_MODEL = data.json, 02_ARCHITECTURE ⊇ components,
 * 그리고 코드 정답 목록(정규식) ⊆ api.json.
 */
export function checkConsistency(docs: Map<string, string>, ws: CurrentWorkspace, truth?: TruthLists): Issue[] {
  const issues: Issue[] = [];
  const apiKeys = new Set(ws.api.endpoints.map((e) => epKey(e.method, e.path)));

  const apiDoc = docs.get('08_API.md');
  if (apiDoc) {
    const docKeys = new Set([...apiDoc.matchAll(EP_ROW_RE)].map((m) => epKey(m[1], m[2])));
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
      const k = epKey(e.method, e.path);
      if (!featureMentions.has(k) && e.featureIds.length) issues.push({ document: '09_FEATURES.md', type: 'ENDPOINT_NOT_IN_FEATURE_DOCS', description: `엔드포인트 ${k}(기능 ${e.featureIds.join(',')})를 어떤 기능 문서도 언급하지 않는다`, evidence: [{ file: e.file }] });
    }
  }

  const dataDoc = docs.get('07_DATA_MODEL.md');
  if (dataDoc) {
    const headings = new Set([...dataDoc.matchAll(/^### ([\w.]+)/gm)].map((m) => m[1]));
    for (const e of ws.data.entities) if (!headings.has(e.name)) issues.push({ document: '07_DATA_MODEL.md', type: 'MISSING_ENTITY_IN_DOC', description: `data.json 엔티티가 문서에 없다: ${e.name}`, evidence: [{ file: e.file }] });
  }

  const archDoc = docs.get('02_ARCHITECTURE.md');
  if (archDoc) {
    for (const c of ws.architecture.components) if (!archDoc.includes(c.name)) issues.push({ document: '02_ARCHITECTURE.md', type: 'MISSING_COMPONENT_IN_DOC', description: `architecture.json 컴포넌트가 문서에 없다: ${c.name}`, evidence: c.evidence });
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
  return issues;
}
