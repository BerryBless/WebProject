import { existsSync, readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileTree, formatTruthLists, truthLists } from '../context.js';
import { ALWAYS_DOCS, DOC_INPUTS, NARRATIVE_DOCS, TEMPLATE_DOCS, featureDocName, type WorkspaceInput } from '../docs-index.js';
import { sha256 } from '../fsx.js';
import { callClaude, type PhaseContext, type Scope } from '../phases/common.js';
import type { Journal } from '../phases/failures.js';
import { today } from '../phases/feature.js';
import { buildPrompt } from '../prompts.js';
import type { Baseline, ChangeSet, CurrentWorkspace, Diagram, DiagramUpdate, FeatureAnalysis, ManualOverride } from '../types.js';
import { DOC_SPECS } from './doc-specs.js';
import { diagramBody, mermaidFromBody, normalizeMermaid, renderDiagramSection } from './diagrams.js';
import { assembleDoc, isManuallyEdited, parseSections, sectionHash, wrapSection, type Section } from './sections.js';
import { renderAdrDocs, renderFeatureDoc, renderTemplateDoc, type RenderExtras } from './templates.js';

export interface DiagramDecision { document: string; diagram: string; decision: 'NEW' | 'UNCHANGED' | 'UPDATED' | 'MANUAL_KEPT' | 'REMOVED'; reason?: string }
export interface ManualEdit { document: string; section: string; kept: boolean }

export interface UpdateDocsResult {
  written: Map<string, string>;
  deleted: string[];
  diagramDecisions: DiagramDecision[];
  manualEdits: ManualEdit[];
  skipped: string[];
}

export interface UpdateDocsOptions {
  ws: CurrentWorkspace;
  journal: Journal;
  scope: Scope;
  /** 기존 정본 문서(docs/generated). 이름 → 본문. */
  prevDocs: Map<string, string>;
  baseline: Baseline | null;
  changes: ChangeSet | null;
  /** 이전 정본의 기능 분석(다이어그램 비교용). */
  prevAnalyses: Map<string, FeatureAnalysis>;
  /** 검증 수정 루프: 문서별 지적 사항. */
  issues?: Map<string, string[]>;
  /** 이 문서들만 다시 만든다(수정 루프). */
  onlyDocs?: Set<string>;
  /** 수정 루프 회차. 같은 이슈로 다시 고칠 때 캐시된 같은 답을 재사용하지 않도록 항목 id에 섞는다. */
  fixAttempt?: number;
}

/** docs/generated를 읽어 이름 → 본문 맵으로. */
export function readDocs(dir: string): Map<string, string> {
  const out = new Map<string, string>();
  if (!existsSync(dir)) return out;
  const walk = (d: string, prefix: string) => {
    for (const e of readdirSync(d, { withFileTypes: true })) {
      if (e.isDirectory()) walk(path.join(d, e.name), `${prefix}${e.name}/`);
      else if (e.name.endsWith('.md')) out.set(`${prefix}${e.name}`, readFileSync(path.join(d, e.name), 'utf8'));
    }
  };
  walk(dir, '');
  return out;
}

/** 문서 입력 선언(DOC_INPUTS)에 따른 입력 해시. 같으면 서술 문서를 다시 만들 이유가 없다. */
export function inputsHash(doc: string, ws: CurrentWorkspace, journal: Journal, truth: string): string {
  const inputs = DOC_INPUTS[doc] ?? [];
  const parts: string[] = [];
  const pick = (i: WorkspaceInput): unknown => {
    switch (i) {
      case 'inventory': return ws.inventory;
      case 'architecture': return ws.architecture;
      case 'features': return ws.features;
      case 'featureAnalyses': return [...ws.featureAnalyses.entries()];
      case 'data': return ws.data;
      case 'api': return ws.api;
      case 'failures': return ws.failures;
      case 'operations': return ws.operations;
      case 'journal': return journal;
      case 'truth': return truth;
    }
  };
  for (const i of inputs) parts.push(JSON.stringify(pick(i)));
  return sha256(parts.join('\u0000'));
}

function manualSections(prev: string | undefined): Map<string, Section> {
  const out = new Map<string, Section>();
  if (!prev) return out;
  for (const s of parseSections(prev).sections) if (isManuallyEdited(s)) out.set(s.id, s);
  return out;
}

/**
 * 렌더된 새 문서에 기존 문서의 수동 수정 섹션을 되돌려 넣는다(증분 모드, 검증 전).
 * 수정 루프에서 해당 섹션에 이슈가 있으면 `overrideSections`로 덮어쓴다.
 */
function preserveManualEdits(doc: string, fresh: string, prev: string | undefined, overrideSections: Set<string>, manualEdits: ManualEdit[], decisions: DiagramDecision[]): string {
  const manual = manualSections(prev);
  if (!manual.size) return fresh;
  const lines = fresh.replace(/\r\n/g, '\n').split('\n');
  const { sections } = parseSections(fresh);
  let out = lines;
  for (const s of [...sections].reverse()) {
    const m = manual.get(s.id);
    if (!m) continue;
    const kept = !overrideSections.has(s.id);
    manualEdits.push({ document: doc, section: s.id, kept });
    if (!kept) continue;
    // 사람이 고친 본문을 유지하고 앵커 해시를 그 본문으로 갱신한다.
    const block = wrapSection(s.id, m.body).split('\n');
    out = [...out.slice(0, s.start), ...block, ...out.slice(s.end + 1)];
    if (mermaidFromBody(m.body)) decisions.push({ document: doc, diagram: s.id, decision: 'MANUAL_KEPT' });
  }
  return out.join('\n');
}

interface DocumentOutput { title: string; oneLiner: string; sections: { id: string; heading: string; body: string; diagrams: Diagram[]; unchanged: boolean }[]; relatedDocs: string[]; unknowns: string[] }

function previousSectionsJson(prev: string | undefined): string {
  if (!prev) return '(없음 — 최초 작성)';
  const { sections } = parseSections(prev);
  return JSON.stringify(sections.map((s) => ({ id: s.id, body: s.body.slice(0, 6000) })), null, 1).slice(0, 60_000);
}

async function renderNarrativeDoc(ctx: PhaseContext, name: string, ws: CurrentWorkspace, prev: string | undefined, truth: string, issues: string[] | undefined, extraHash: string, fixAttempt = 0): Promise<{ md: string; diagrams: Diagram[] }> {
  const spec = DOC_SPECS[name];
  if (!spec) throw new Error(`서술 문서 사양이 없다: ${name}`);
  const prompt = buildPrompt('08_document', {
    docName: name, title: spec.title, purpose: spec.purpose,
    sectionsSpec: spec.sections.map((s) => `- \`${s.id}\` **${s.heading}** — ${s.guidance}`).join('\n'),
    inputs: JSON.stringify(spec.inputs(ws), null, 1).slice(0, 70_000),
    truthLists: name === '05_CONFIGURATION.md' || name === '08_API.md' ? truth : '(이 문서에는 제공하지 않음)',
    readHints: spec.readHints,
    docNames: [...TEMPLATE_DOCS, ...NARRATIVE_DOCS, ...ws.features.features.filter((f) => f.status !== 'REMOVED').map((f) => featureDocName(f.id, f.slug))].sort().map((d) => `- ${d}`).join('\n'),
    previous: previousSectionsJson(prev),
    issues: issues?.length ? issues.map((i) => `- ${i}`).join('\n') : '(없음)',
    today: today(),
  });
  const out = await callClaude<DocumentOutput>(ctx, { itemId: `doc:${name}${issues?.length ? ':fix' + sha256(issues.join('|')).slice(0, 6) + (fixAttempt ? `-${fixAttempt}` : '') : ''}`, phase: spec.phase, schemaName: 'document', prompt, outFile: `docs/${name}.json`, extraHash });
  const prevSections = prev ? new Map(parseSections(prev).sections.map((s) => [s.id, s])) : new Map<string, Section>();
  const sections: { id: string; body: string }[] = [];
  const diagrams: Diagram[] = [];
  sections.push({ id: 'one-liner', body: out.oneLiner.trim() });
  for (const s of out.sections) {
    if (s.unchanged && prevSections.has(s.id)) {
      sections.push({ id: s.id, body: prevSections.get(s.id)!.body });
      // 이전 문서에서 이 섹션 바로 뒤에 붙어 있던 다이어그램 섹션도 유지한다.
      for (const d of followingDiagramSections(prev!, s.id)) sections.push({ id: d.id, body: d.body });
      continue;
    }
    sections.push({ id: s.id, body: `## ${s.heading}\n\n${s.body.trim()}` });
    for (const d of s.diagrams) { diagrams.push(d); sections.push(renderDiagramSection(d, 3)); }
  }
  if (out.unknowns.length) sections.push({ id: 'unknowns', body: ['## 확인하지 못한 것', '', ...out.unknowns.map((u) => `- ${u}`)].join('\n') });
  return { md: assembleDoc(out.title || spec.title, sections), diagrams };
}

/** 이전 문서에서 섹션 id 바로 다음에 이어지는 다이어그램 섹션들(대문자 id). */
function followingDiagramSections(prev: string, afterId: string): Section[] {
  const { sections } = parseSections(prev);
  const idx = sections.findIndex((s) => s.id === afterId);
  const out: Section[] = [];
  for (let i = idx + 1; i < sections.length; i++) {
    if (/^[A-Z][A-Z0-9_]+$/.test(sections[i].id) && mermaidFromBody(sections[i].body)) out.push(sections[i]);
    else break;
  }
  return out;
}

/**
 * 다이어그램 조정: 기존 문서의 mermaid와 새 분석의 mermaid가 같으면 기존 유지, 다르면 I08로 판정한다.
 * 결과 섹션 본문은 항상 새 분석의 summary/details/nodes 골격에 판정된 mermaid를 넣는다.
 */
async function reconcileDiagram(ctx: PhaseContext, doc: string, d: Diagram, prevSection: Section | undefined, fa: FeatureAnalysis | null, changes: ChangeSet | null, decisions: DiagramDecision[], headingLevel: number): Promise<string> {
  const existing = prevSection ? mermaidFromBody(prevSection.body) : null;
  if (!existing) { decisions.push({ document: doc, diagram: d.id, decision: 'NEW' }); return diagramBody(d, headingLevel); }
  if (normalizeMermaid(existing) === normalizeMermaid(d.mermaid)) { decisions.push({ document: doc, diagram: d.id, decision: 'UNCHANGED' }); return diagramBody({ ...d, mermaid: existing }, headingLevel); }
  const related = new Set(d.nodes.map((n) => n.code));
  const hunks = changes?.files.filter((f) => related.has(f.file)).slice(0, 6).map((f) => `### ${f.changeType} ${f.file}\n\`\`\`diff\n${f.removedLines.slice(0, 30).map((l) => `- ${l}`).join('\n')}\n${f.addedLines.slice(0, 30).map((l) => `+ ${l}`).join('\n')}\n\`\`\``).join('\n\n') || '(이 다이어그램 노드의 파일에 직접 변경 없음)';
  const prompt = buildPrompt('I08_diagram_update', {
    diagramId: d.id, existing: existing.trim(), proposed: d.mermaid.trim(),
    analysisSummary: fa ? JSON.stringify({ executionFlow: fa.executionFlow, dataFlow: fa.dataFlow.map((c) => c.text), stateTransitions: fa.stateTransitions }, null, 1).slice(0, 20_000) : d.summary,
    changeHints: hunks,
  });
  const upd = await callClaude<DiagramUpdate>(ctx, { itemId: `diagram:${d.id}`, phase: 'diagram', schemaName: 'diagram_update', prompt, outFile: `diagrams/${d.id}.json`, extraHash: sha256(existing + d.mermaid) });
  if (upd.decision === 'UNCHANGED') { decisions.push({ document: doc, diagram: d.id, decision: 'UNCHANGED', reason: upd.reason }); return diagramBody({ ...d, mermaid: existing }, headingLevel); }
  decisions.push({ document: doc, diagram: d.id, decision: 'UPDATED', reason: upd.reason });
  return diagramBody({ ...d, mermaid: upd.mermaid, nodes: upd.nodes.length ? upd.nodes : d.nodes }, headingLevel);
}

async function renderFeatureWithDiagrams(ctx: PhaseContext, fa: FeatureAnalysis, prev: string | undefined, changes: ChangeSet | null, decisions: DiagramDecision[], incremental: boolean): Promise<{ name: string; md: string }> {
  const { name, md } = renderFeatureDoc(fa);
  if (!incremental || !prev) { for (const d of fa.diagrams) decisions.push({ document: name, diagram: d.id, decision: 'NEW' }); return { name, md }; }
  const prevSections = new Map(parseSections(prev).sections.map((s) => [s.id, s]));
  let out = md;
  for (const d of fa.diagrams) {
    const body = await reconcileDiagram(ctx, name, d, prevSections.get(d.id), fa, changes, decisions, 2);
    out = replaceBody(out, d.id, body);
  }
  for (const [id, s] of prevSections) if (/^[A-Z][A-Z0-9_]+$/.test(id) && !fa.diagrams.some((d) => d.id === id) && mermaidFromBody(s.body)) decisions.push({ document: name, diagram: id, decision: 'REMOVED' });
  return { name, md: out };
}

function replaceBody(md: string, id: string, body: string): string {
  const { sections } = parseSections(md);
  const t = sections.find((s) => s.id === id);
  if (!t) return md;
  const lines = md.split('\n');
  return [...lines.slice(0, t.start), ...wrapSection(id, body).split('\n'), ...lines.slice(t.end + 1)].join('\n');
}

/**
 * 문서 갱신(스펙 §6.6·§6.5). ALL이면 전부, PARTIAL이면 영향 문서 + 입력이 바뀐 서술 문서 + 템플릿 문서(항상, 비용 0).
 * 결과는 staging/docs에 쓰지 않고 맵으로 돌려준다(호출자가 검증 후 씀).
 */
export async function updateDocs(ctx: PhaseContext, opts: UpdateDocsOptions): Promise<UpdateDocsResult> {
  const { ws, journal, scope, prevDocs, baseline, changes, issues } = opts;
  const incremental = scope.kind === 'PARTIAL';
  const truthText = formatTruthLists(truthLists(ctx.paths.projectRoot, ctx.cfg));
  const extras: RenderExtras = { journal, prevDocs, fileTree: fileTree(ctx.paths.projectRoot, ctx.cfg), today: today() };
  const written = new Map<string, string>();
  const deleted: string[] = [];
  const decisions: DiagramDecision[] = [];
  const manualEdits: ManualEdit[] = [];
  const skipped: string[] = [];
  const affectedDocs = new Set(scope.kind === 'PARTIAL' ? [...scope.docs, ...ALWAYS_DOCS] : []);
  const wanted = (name: string) => !opts.onlyDocs || opts.onlyDocs.has(name);
  const overrideFor = (name: string) => new Set((issues?.get(name) ?? []).map((i) => /section=([\w:-]+)/.exec(i)?.[1]).filter((x): x is string => !!x));
  const finalize = (name: string, md: string) => {
    const merged = incremental ? preserveManualEdits(name, md, prevDocs.get(name), overrideFor(name), manualEdits, decisions) : md;
    if (prevDocs.get(name)?.replace(/\r\n/g, '\n') === merged) { skipped.push(name); return; }
    written.set(name, merged);
  };

  // 1) 기능 문서
  const activeFeatures = ws.features.features.filter((f) => f.status !== 'REMOVED');
  for (const f of activeFeatures) {
    const fa = ws.featureAnalyses.get(f.id);
    const name = featureDocName(f.id, f.slug);
    if (!fa || !wanted(name)) continue;
    const inScope = !incremental || affectedDocs.has(name) || scope.features.includes(f.id) || !prevDocs.has(name) || (issues?.has(name) ?? false);
    if (!inScope) { skipped.push(name); continue; }
    const r = await renderFeatureWithDiagrams(ctx, fa, prevDocs.get(name), changes, decisions, incremental);
    finalize(r.name, r.md);
  }
  for (const f of ws.features.features.filter((x) => x.status === 'REMOVED')) {
    const name = featureDocName(f.id, f.slug);
    if (prevDocs.has(name)) deleted.push(name);
  }
  // 이름이 바뀐 기능 문서(slug 변경) 정리
  for (const prevName of prevDocs.keys()) {
    if (!prevName.startsWith('features/')) continue;
    const id = /^features\/(F\d{3})_/.exec(prevName)?.[1];
    const f = id ? ws.features.features.find((x) => x.id === id) : undefined;
    if (f && f.status !== 'REMOVED' && featureDocName(f.id, f.slug) !== prevName) deleted.push(prevName);
  }

  // 2) 템플릿 문서(항상 렌더, 같으면 skip). 템플릿 안의 다이어그램(ER 등)은 워크스페이스가 원본이므로 판정 없이 기록만 한다.
  for (const name of TEMPLATE_DOCS) {
    if (!wanted(name)) continue;
    const md = renderTemplateDoc(name, ws, extras);
    const prev = prevDocs.get(name);
    const prevSections = prev ? new Map(parseSections(prev).sections.map((s) => [s.id, s])) : new Map<string, Section>();
    for (const s of parseSections(md).sections) {
      const code = mermaidFromBody(s.body);
      if (!code || !/^[A-Z][A-Z0-9_]+$/.test(s.id)) continue;
      const prevCode = prevSections.get(s.id) ? mermaidFromBody(prevSections.get(s.id)!.body) : null;
      decisions.push({ document: name, diagram: s.id, decision: !prevCode ? 'NEW' : normalizeMermaid(prevCode) === normalizeMermaid(code) ? 'UNCHANGED' : 'UPDATED' });
    }
    finalize(name, md);
  }
  for (const adr of renderAdrDocs(ws)) if (wanted(adr.name)) finalize(adr.name, adr.md);

  // 3) 서술 문서: ALL이면 전부, PARTIAL이면 영향 문서 또는 입력 해시가 바뀐 문서
  for (const name of NARRATIVE_DOCS) {
    if (!wanted(name)) continue;
    const hash = inputsHash(name, ws, journal, truthText);
    const prev = prevDocs.get(name);
    const changedInputs = baseline?.documents[name]?.inputsHash !== hash;
    const inScope = !incremental || affectedDocs.has(name) || changedInputs || !prev || (issues?.has(name) ?? false);
    if (!inScope) { skipped.push(name); continue; }
    const r = await renderNarrativeDoc(ctx, name, ws, incremental ? prev : undefined, truthText, issues?.get(name), hash, opts.fixAttempt ?? 0);
    let md = r.md;
    if (incremental && prev) {
      const prevSections = new Map(parseSections(prev).sections.map((s) => [s.id, s]));
      for (const d of r.diagrams) md = replaceBody(md, d.id, await reconcileDiagram(ctx, name, d, prevSections.get(d.id), null, changes, decisions, 3));
    } else {
      for (const d of r.diagrams) decisions.push({ document: name, diagram: d.id, decision: 'NEW' });
    }
    finalize(name, md);
  }
  return { written, deleted, diagramDecisions: decisions, manualEdits, skipped };
}

/** baseline.documents 항목 계산(정본 문서 기준). */
export function documentBaselineEntry(md: string, doc: string, ws: CurrentWorkspace, journal: Journal, truth: string): Baseline['documents'][string] {
  const sections: Record<string, string> = {};
  for (const s of parseSections(md).sections) sections[s.id] = sectionHash(s.body);
  return { hash: sha256(md), inputsHash: inputsHash(doc, ws, journal, truth), sections };
}
