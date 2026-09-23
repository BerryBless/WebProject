import { sha256 } from '../fsx.js';
import { callClaude, type PhaseContext } from '../phases/common.js';
import { buildPrompt } from '../prompts.js';
import type { CurrentWorkspace, Issue, Verification } from '../types.js';

export type VerificationPartial = Omit<Verification, 'mermaidParser' | 'iterations' | 'passed'>;

export interface DocGroup { name: string; docs: Map<string, string> }

const GROUP_ORDER: { name: string; match: (doc: string) => boolean }[] = [
  { name: 'overview', match: (d) => /^(README|00_|01_|02_|03_|04_|05_|06_)/.test(d) || d.startsWith('adr/') },
  { name: 'data-api', match: (d) => /^(07_|08_)/.test(d) },
  { name: 'features', match: (d) => d.startsWith('features/') || d.startsWith('09_') },
  { name: 'operations', match: (d) => /^(1\d_|20_)/.test(d) },
];

const MAX_GROUP_CHARS = 140_000;

/** 문서를 검증 그룹으로 나눈다. 그룹이 너무 크면 순서를 유지한 채 여러 조각으로. changed가 있으면 그 문서가 든 그룹만. */
export function groupDocs(docs: Map<string, string>, changed: Set<string> | null): DocGroup[] {
  const groups: DocGroup[] = [];
  for (const g of GROUP_ORDER) {
    const members = [...docs.keys()].filter((d) => g.match(d)).sort();
    const selected = changed ? members.filter((d) => changed.has(d)) : members;
    if (!selected.length) continue;
    let chunk: DocGroup = { name: g.name, docs: new Map() };
    let size = 0;
    let part = 1;
    for (const d of selected) {
      const md = docs.get(d)!;
      if (size + md.length > MAX_GROUP_CHARS && chunk.docs.size) {
        groups.push(chunk);
        part++;
        chunk = { name: `${g.name}-${part}`, docs: new Map() };
        size = 0;
      }
      chunk.docs.set(d, md);
      size += md.length;
    }
    if (chunk.docs.size) groups.push(chunk);
  }
  return groups;
}

function docsBlock(docs: Map<string, string>): string {
  return [...docs.entries()].map(([name, md]) => `<<<<< ${name}\n${md.trim()}\n>>>>> ${name}`).join('\n\n');
}

export function workspaceSummary(ws: CurrentWorkspace): string {
  return JSON.stringify({
    features: ws.features.features.map((f) => ({ id: f.id, name: f.name, status: f.status, entryPoints: f.entryPoints, relatedFiles: f.relatedFiles })),
    components: ws.architecture.components.map((c) => ({ id: c.id, name: c.name, path: c.path })),
    endpoints: ws.api.endpoints.map((e) => `${e.method} ${e.path} (${e.file})`),
    entities: ws.data.entities.map((e) => `${e.name} (${e.file})`),
  }, null, 1).slice(0, 40_000);
}

export async function verifyLlm(ctx: PhaseContext, group: DocGroup, ws: CurrentWorkspace, truthText: string, allDocNames: string[], iteration: number): Promise<VerificationPartial> {
  const prompt = buildPrompt('09_verification', {
    groupName: group.name,
    truthLists: truthText,
    summary: workspaceSummary(ws),
    docs: docsBlock(group.docs),
    otherDocs: `## 그 밖의 생성 문서(이름만)\n\n${allDocNames.filter((d) => !group.docs.has(d)).map((d) => `- ${d}`).join('\n') || '(없음)'}`,
  });
  const contentHash = sha256([...group.docs.values()].join('\u0000'));
  return callClaude<VerificationPartial>(ctx, { itemId: `verify:${group.name}:${iteration}`, phase: 'verification', schemaName: 'verification', prompt, outFile: `verification/${group.name}-${iteration}.json`, extraHash: contentHash });
}

export async function consistencyLlm(ctx: PhaseContext, changedDocs: Map<string, string>, relatedDocs: Map<string, string>, iteration: number): Promise<Issue[]> {
  if (!changedDocs.size) return [];
  const prompt = buildPrompt('09_consistency', {
    changedDocs: docsBlock(changedDocs).slice(0, 120_000),
    relatedDocs: relatedDocs.size ? docsBlock(relatedDocs).slice(0, 100_000) : '(없음)',
  });
  const out = await callClaude<{ issues: Issue[]; summary: string }>(ctx, { itemId: `consistency:${iteration}`, phase: 'verification', schemaName: 'consistency', prompt, outFile: `verification/consistency-${iteration}.json`, extraHash: sha256([...changedDocs.values(), ...relatedDocs.values()].join('\u0000')) });
  return out.issues.map((i) => ({ ...i, type: i.type || 'INCONSISTENT' }));
}

/** 갱신된 문서가 참조하거나 같은 기능을 다루는 미변경 문서(일관성 검사 대상). */
export function relatedUnchangedDocs(changed: Map<string, string>, all: Map<string, string>): Map<string, string> {
  const out = new Map<string, string>();
  const changedFeatureIds = new Set<string>();
  for (const [name, md] of changed) {
    for (const m of md.matchAll(/\[[^\]]*\]\(([^)#\s]+\.md)\)/g)) {
      const target = m[1].replace(/^\.\.\//, '').replace(/^\.\//, '');
      const resolved = name.startsWith('features/') && !target.includes('/') ? target : target;
      if (all.has(resolved) && !changed.has(resolved)) out.set(resolved, all.get(resolved)!);
    }
    for (const m of md.matchAll(/\bF\d{3}\b/g)) changedFeatureIds.add(m[0]);
  }
  for (const [name, md] of all) {
    if (changed.has(name) || out.has(name)) continue;
    if (/^(02_ARCHITECTURE|07_DATA_MODEL|08_API)\.md$/.test(name)) out.set(name, md);
    else if (name.startsWith('features/') && [...changedFeatureIds].some((id) => name.includes(id))) out.set(name, md);
  }
  // 크기 상한: 작은 문서부터 담고 상한을 넘기는 큰 문서만 뺀다.
  let total = 0;
  for (const [name, md] of [...out.entries()].sort((a, b) => a[1].length - b[1].length)) {
    if (total + md.length > 100_000) out.delete(name);
    else total += md.length;
  }
  return out;
}
