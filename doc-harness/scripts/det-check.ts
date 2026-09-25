// 결정적 검사 재현: staging/current에서 문서를 렌더해 참조·Mermaid·일관성 검사를 돌리고 유형별 분포를 찍는다.
import { readdirSync, readFileSync, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { loadConfig, resolvePaths } from '../src/config.js';
import { loadWorkspace } from '../src/phases/common.js';
import { renderFeatureDoc, renderTemplateDoc } from '../src/render/templates.js';
import { TEMPLATE_DOCS } from '../src/docs-index.js';
import { checkMermaid } from '../src/verify/mermaid.js';
import { checkReferences } from '../src/verify/references.js';
import { checkConsistency } from '../src/verify/consistency.js';
import { truthLists, fileTree } from '../src/context.js';
import { assembleDoc } from '../src/render/sections.js';
import { renderDiagramSection } from '../src/render/diagrams.js';

const cfg = loadConfig(); const paths = resolvePaths(cfg);
const cur = path.join(paths.runs, 'run-0001', 'staging', 'current');
const ws = await loadWorkspace(cur);
const journal = JSON.parse(readFileSync(path.join(cur, 'journal.json'), 'utf8'));
const docs = new Map<string, string>();
for (const fa of ws.featureAnalyses.values()) { const { name, md } = renderFeatureDoc(fa); docs.set(name, md); }
for (const name of TEMPLATE_DOCS) docs.set(name, renderTemplateDoc(name, ws, { journal, prevDocs: new Map(), fileTree: fileTree(paths.projectRoot, cfg), today: '2026-09-24' }));
// 서술 문서: 캐시 JSON에서 섹션과 다이어그램만 조립(검증용 근사)
const ddir = path.join(cur, 'docs');
for (const f of readdirSync(ddir)) {
  const name = f.replace(/\.json$/, '');
  const j = JSON.parse(readFileSync(path.join(ddir, f), 'utf8'));
  const sections: { id: string; body: string }[] = [{ id: 'one-liner', body: j.oneLiner }];
  for (const s of j.sections) { sections.push({ id: s.id, body: `## ${s.heading}\n\n${s.body}` }); for (const d of s.diagrams ?? []) sections.push(renderDiagramSection(d, 3)); }
  docs.set(name, assembleDoc(j.title, sections));
}
const out = path.join(paths.workspace, 'det-check');
mkdirSync(out, { recursive: true });
for (const [n, md] of docs) { mkdirSync(path.dirname(path.join(out, n)), { recursive: true }); writeFileSync(path.join(out, n), md); }
const refs = checkReferences(docs, paths.projectRoot, ws, cfg);
const mer = await checkMermaid(docs, ws, cfg, paths.projectRoot);
const cons = checkConsistency(docs, ws, truthLists(paths.projectRoot, cfg));
const count = (xs: { type: string }[]) => { const c: Record<string, number> = {}; for (const x of xs) c[x.type] = (c[x.type] ?? 0) + 1; return c; };
console.log('docs', docs.size, 'parser', mer.parser);
console.log('references issues', count(refs.issues), 'warnings', count(refs.warnings));
console.log('mermaid issues', count(mer.issues), 'warnings', count(mer.warnings));
console.log('consistency issues', count(cons.issues), 'warnings', count(cons.warnings));
const sample = (t: string, n = 6) => mer.issues.filter((i) => i.type === t).slice(0, n).map((i) => `  - ${i.document} ${i.diagram}: ${i.description.slice(0, 140)}`).join('\n');
for (const t of Object.keys(count(mer.issues))) console.log(`\n[${t}]\n${sample(t)}`);
console.log('\n[refs]\n' + [...refs.issues, ...refs.warnings].slice(0, 12).map((i) => `  - ${i.document} ${i.type}: ${i.description.slice(0, 120)}`).join('\n'));
console.log('\n[cons]\n' + [...cons.issues, ...cons.warnings].slice(0, 12).map((i) => `  - ${i.document} ${i.type}: ${i.description.slice(0, 120)}`).join('\n'));
