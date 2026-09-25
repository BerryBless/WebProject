// 지정 Run의 스테이징 산출물 + 정본 문서(docs/generated)로 결정적 검사를 재현한다: npx tsx scripts/repro-det.ts run-0002
import path from 'node:path';
import { loadConfig, resolvePaths } from '../src/config.js';
import { loadWorkspace } from '../src/phases/common.js';
import { readDocs } from '../src/render/documents.js';
import { renderApiDoc, renderTemplateDoc } from '../src/render/templates.js';
import { TEMPLATE_DOCS } from '../src/docs-index.js';
import { checkReferences } from '../src/verify/references.js';
import { checkConsistency } from '../src/verify/consistency.js';
import { checkMermaid } from '../src/verify/mermaid.js';
import { truthLists, fileTree } from '../src/context.js';
import { readFileSync } from 'node:fs';

const run = process.argv[2] ?? 'run-0002';
const cfg = loadConfig(); const paths = resolvePaths(cfg);
const cur = path.join(paths.runs, run, 'staging', 'current');
const ws = await loadWorkspace(cur);
const journal = JSON.parse(readFileSync(path.join(cur, 'journal.json'), 'utf8'));
const docs = readDocs(paths.docsOut);
for (const name of TEMPLATE_DOCS) docs.set(name, renderTemplateDoc(name, ws, { journal, prevDocs: docs, fileTree: fileTree(paths.projectRoot, cfg), today: '2026-09-25' }));
const truth = truthLists(paths.projectRoot, cfg);
const refs = checkReferences(docs, paths.projectRoot, ws, cfg);
const cons = checkConsistency(docs, ws, truth);
const mer = await checkMermaid(docs, ws, cfg, paths.projectRoot);
const all = [...refs.issues, ...cons.issues, ...mer.issues];
console.log('blocking-ish issues:', all.length);
for (const i of all) console.log(' -', i.document, i.type, '|', i.description.slice(0, 150));
