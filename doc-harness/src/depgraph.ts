import { ALWAYS_DOCS, DOC_INPUTS, featureDocName, type WorkspaceInput } from './docs-index.js';
import type { CurrentWorkspace, Depgraph } from './types.js';

function add(map: Record<string, string[]>, key: string, value: string): void {
  const arr = (map[key] ??= []);
  if (!arr.includes(value)) arr.push(value);
}

/**
 * 워크스페이스의 evidence에서 Source ↔ Feature ↔ API/Data ↔ Diagram ↔ Document 그래프를 유도한다(스펙 §7).
 * LLM이 만들지 않는다. 매 Run의 마지막에 다시 계산한다.
 */
export function buildDepgraph(ws: CurrentWorkspace): Depgraph {
  const files: Depgraph['files'] = {};
  const features: Depgraph['features'] = {};
  const documents: Depgraph['documents'] = {};
  const inputToDocs = new Map<WorkspaceInput, string[]>();
  for (const [doc, inputs] of Object.entries(DOC_INPUTS)) for (const i of inputs) inputToDocs.set(i, [...(inputToDocs.get(i) ?? []), doc]);

  const ensureFile = (f: string) => (files[f] ??= { features: [], apis: [], entities: [], diagrams: [], documents: [] });
  const ensureFeature = (id: string) => (features[id] ??= { files: [], apis: [], entities: [], diagrams: [], documents: [], dependencies: [] });

  for (const f of ws.features.features) {
    const node = ensureFeature(f.id);
    node.dependencies = [...f.dependencies];
    const doc = featureDocName(f.id, f.slug);
    node.documents.push(doc, ...(inputToDocs.get('featureAnalyses') ?? []), ...(inputToDocs.get('features') ?? []));
    documents[doc] = { inputs: ['featureAnalyses'], features: [f.id], files: [] };
    for (const file of f.relatedFiles) { add(node as unknown as Record<string, string[]>, 'files', file); add(ensureFile(file) as unknown as Record<string, string[]>, 'features', f.id); }
    const fa = ws.featureAnalyses.get(f.id);
    if (fa) {
      const codeFiles = new Set<string>([...fa.relatedCode.map((c) => c.file), ...fa.executionFlow.map((s) => s.file), ...fa.evidence.map((e) => e.file)]);
      for (const file of codeFiles) { add(node as unknown as Record<string, string[]>, 'files', file); add(ensureFile(file) as unknown as Record<string, string[]>, 'features', f.id); }
      for (const d of fa.diagrams) { node.diagrams.push(d.id); for (const n of d.nodes) add(ensureFile(n.code) as unknown as Record<string, string[]>, 'diagrams', d.id); }
      for (const db of fa.databaseAccess) add(node as unknown as Record<string, string[]>, 'entities', db.entity);
      documents[doc].files = [...codeFiles];
    }
  }
  for (const ep of ws.api.endpoints) {
    const key = `${ep.method} ${ep.path}`;
    add(ensureFile(ep.file) as unknown as Record<string, string[]>, 'apis', key);
    for (const id of ep.featureIds) add(ensureFeature(id) as unknown as Record<string, string[]>, 'apis', key);
  }
  for (const e of ws.data.entities) {
    add(ensureFile(e.file) as unknown as Record<string, string[]>, 'entities', e.name);
    for (const n of e.evidence) add(ensureFile(n.file) as unknown as Record<string, string[]>, 'entities', e.name);
  }
  for (const d of [...ws.architecture.diagrams, ...ws.data.diagrams]) for (const n of d.nodes) add(ensureFile(n.code) as unknown as Record<string, string[]>, 'diagrams', d.id);
  for (const c of ws.architecture.components) for (const e of c.evidence) ensureFile(e.file);

  // 파일 → 문서: 기능 문서 + API/데이터 문서 + 아키텍처 컴포넌트 문서
  for (const [file, node] of Object.entries(files)) {
    for (const id of node.features) for (const doc of features[id]?.documents ?? []) add(node as unknown as Record<string, string[]>, 'documents', doc);
    if (node.apis.length) for (const doc of inputToDocs.get('api') ?? []) add(node as unknown as Record<string, string[]>, 'documents', doc);
    if (node.entities.length) for (const doc of inputToDocs.get('data') ?? []) add(node as unknown as Record<string, string[]>, 'documents', doc);
    if (ws.architecture.components.some((c) => c.path === file || c.evidence.some((e) => e.file === file))) for (const doc of inputToDocs.get('architecture') ?? []) add(node as unknown as Record<string, string[]>, 'documents', doc);
  }
  for (const [doc, inputs] of Object.entries(DOC_INPUTS)) {
    documents[doc] = { inputs, features: inputs.includes('features') || inputs.includes('featureAnalyses') ? Object.keys(features) : [], files: [] };
  }
  return { files, features, documents };
}

/** 변경 파일 집합에서 직접 영향받는 기능. */
export function featuresForFiles(graph: Depgraph, changedFiles: string[]): string[] {
  const out = new Set<string>();
  for (const f of changedFiles) for (const id of graph.files[f]?.features ?? []) out.add(id);
  return [...out].sort();
}

/** 기능 집합에서 1홉 의존(역방향 포함) 확장. */
export function expandByDependencies(graph: Depgraph, featureIds: string[]): string[] {
  const out = new Set(featureIds);
  for (const id of featureIds) {
    for (const dep of graph.features[id]?.dependencies ?? []) out.add(dep);
    for (const [other, node] of Object.entries(graph.features)) if (node.dependencies.includes(id)) out.add(other);
  }
  return [...out].sort();
}

export function docsForFeatures(graph: Depgraph, featureIds: string[]): string[] {
  const out = new Set<string>(ALWAYS_DOCS);
  for (const id of featureIds) for (const d of graph.features[id]?.documents ?? []) out.add(d);
  return [...out].sort();
}

export function diagramsForFeatures(graph: Depgraph, featureIds: string[], changedFiles: string[]): string[] {
  const out = new Set<string>();
  for (const id of featureIds) for (const d of graph.features[id]?.diagrams ?? []) out.add(d);
  for (const f of changedFiles) for (const d of graph.files[f]?.diagrams ?? []) out.add(d);
  return [...out].sort();
}
