import { existsSync } from 'node:fs';
import { mkdir, readFile, rename, rm } from 'node:fs/promises';
import path from 'node:path';
import { emptyBaseline, loadBaseline, saveBaseline } from './baseline.js';
import { classifyChanges } from './change/classify.js';
import { detectChanges, hashSourceFiles } from './change/detect.js';
import { applyFeatureDelta, runFeatureDelta } from './change/featureDelta.js';
import { analyzeImpact } from './change/impact.js';
import { CliClaudeRunner, type ClaudeRequest, type ClaudeResult, type ClaudeRunner } from './claude.js';
import { loadConfig, resolvePaths, type HarnessConfig, type HarnessPaths } from './config.js';
import { fileTree, formatTruthLists, truthLists } from './context.js';
import { buildDepgraph, featuresForFiles } from './depgraph.js';
import { atomicWriteJson, atomicWriteText, hashFile, readJsonOrNull, sha256 } from './fsx.js';
import { extractGitHistory } from './git-extract.js';
import { decideMode } from './mode.js';
import { runArchitecture } from './phases/architecture.js';
import { callClaude, loadWorkspace, readStaging, seedStagingFromCurrent, writeStaging, type PhaseContext, type Scope } from './phases/common.js';
import { runApi, runData } from './phases/dataApi.js';
import { runDiscovery } from './phases/discovery.js';
import { appendJournal, loadJournal, runFailuresDelta, runFailuresInitial, seedJournalFromFailures, type Journal } from './phases/failures.js';
import { runFeatures, today } from './phases/feature.js';
import { runInventory } from './phases/inventory.js';
import { runOperations } from './phases/operations.js';
import { documentBaselineEntry, readDocs, updateDocs, type UpdateDocsResult } from './render/documents.js';
import { extractMermaidBlocks, parseSections, sectionHash } from './render/sections.js';
import { formatReport, formatStatus, formatUpToDate, type ReportExtras } from './report.js';
import { Run } from './run.js';
import { totalCost } from './state.js';
import type { Architecture, Baseline, ChangeSet, Classification, CurrentWorkspace, FeatureDelta, FeaturesFile, Impact, Inventory, ManualOverride, RunMode, RunRecord, Verification } from './types.js';
import { makeFixer } from './verify/fixer.js';
import { runVerificationPass, verificationLoop } from './verify/loop.js';

export interface PipelineOptions {
  mode: 'auto' | 'full';
  /** 이 phase까지만 돌리고 커밋하지 않는다(개발·검토용): inventory | architecture | discovery | features | data | api | failures | operations | docs */
  phase?: string;
  /** 이 기능만 분석하고 멈춘다(discovery까지는 돈다). */
  feature?: string;
  sessionContextPath?: string;
  runner?: ClaudeRunner;
  cfg?: HarnessConfig;
  paths?: HarnessPaths;
  log?: (msg: string) => void;
}

export interface PipelineSummary {
  status: 'SUCCESS' | 'FAILED' | 'UP_TO_DATE' | 'PARTIAL';
  mode: RunMode | 'UP_TO_DATE';
  runName: string | null;
  report: string;
  record: RunRecord | null;
}

const PHASE_ORDER = ['inventory', 'architecture', 'discovery', 'features', 'data', 'api', 'failures', 'operations', 'docs', 'verify'];

class CountingRunner implements ClaudeRunner {
  calls = 0;
  constructor(private readonly inner: ClaudeRunner) {}
  async run(req: ClaudeRequest): Promise<ClaudeResult> {
    this.calls++;
    return this.inner.run(req);
  }
}

async function readSessionContext(paths: HarnessPaths, explicit: string | undefined, run: Run): Promise<string> {
  const candidates = [explicit, path.join(paths.inbox, 'session_context.md')].filter((x): x is string => !!x);
  for (const file of candidates) {
    if (!existsSync(file)) continue;
    const text = await readFile(file, 'utf8');
    // 소비한 인박스는 Run 디렉터리로 옮겨 다음 Run에 다시 쓰이지 않게 한다.
    if (file.startsWith(paths.inbox)) await rename(file, path.join(run.dir, 'session_context.md')).catch(() => undefined);
    return text;
  }
  return '';
}

function emptyRecord(run: Run, mode: RunMode, baseline: Baseline | null): RunRecord {
  return {
    run: run.number, name: run.name, mode, status: 'RUNNING', startedAt: run.state.startedAt, completedAt: '', baselineBefore: baseline?.baselineCommit ?? null, baselineAfter: null,
    changedFiles: [], affectedFeatures: [], newFeatures: [], removedFeatures: [], updatedDocuments: [], deletedDocuments: [], updatedDiagrams: [], unchangedDiagrams: [], failuresDiscovered: [],
    manualEditsOverridden: [], verification: null, costUsd: 0, claudeCalls: 0,
  };
}

function diagramCounts(docs: Map<string, string>): Record<string, number> {
  const counts: Record<string, number> = { Architecture: 0, Sequence: 0, Flowchart: 0, 'Data Flow': 0, ER: 0, State: 0, Class: 0 };
  for (const md of docs.values()) {
    for (const b of extractMermaidBlocks(md)) {
      const t = b.code.trim().split(/\s+/)[0];
      const id = b.sectionId ?? '';
      if (t === 'sequenceDiagram') counts.Sequence++;
      else if (t === 'stateDiagram-v2' || t === 'stateDiagram') counts.State++;
      else if (t === 'erDiagram') counts.ER++;
      else if (t === 'classDiagram') counts.Class++;
      else if (/DATAFLOW/.test(id)) counts['Data Flow']++;
      else if (/^ARCH|TOPOLOGY|^DEPLOY/.test(id)) counts.Architecture++;
      else counts.Flowchart++;
    }
  }
  return counts;
}

function buildBaseline(prev: Baseline | null, run: Run, ws: CurrentWorkspace, journal: Journal, docs: Map<string, string>, changes: ChangeSet, projectRoot: string, cfg: HarnessConfig, truthText: string): { baseline: Baseline; depgraph: ReturnType<typeof buildDepgraph> } {
  const graph = buildDepgraph(ws);
  const b: Baseline = {
    ...emptyBaseline(changes.headCommit, changes.fingerprint),
    documentationVersion: (prev?.documentationVersion ?? 0) + 1,
    lastSuccessfulRun: run.name,
    lastSuccessfulAt: new Date().toISOString(),
  };
  for (const f of hashSourceFiles(projectRoot, cfg)) {
    const node = graph.files[f.path];
    b.files[f.path] = { hash: f.hash, features: node?.features ?? [], documents: node?.documents ?? [], diagrams: node?.diagrams ?? [] };
  }
  for (const f of ws.features.features) {
    const fa = ws.featureAnalyses.get(f.id);
    b.features[f.id] = {
      analysisHash: fa ? sha256(JSON.stringify(fa)) : '',
      sourceHash: sha256(f.relatedFiles.map((rf) => `${rf}:${existsSync(path.join(projectRoot, rf)) ? hashFile(path.join(projectRoot, rf)) : 'missing'}`).join('|')),
      analyzedAt: prev?.features[f.id]?.analyzedAt && prev.features[f.id].analysisHash === (fa ? sha256(JSON.stringify(fa)) : '') ? prev.features[f.id].analyzedAt : b.lastSuccessfulAt,
      status: f.status,
    };
  }
  for (const [name, md] of docs) {
    b.documents[name] = documentBaselineEntry(md, name, ws, journal, truthText);
    for (const s of parseSections(md).sections) if (/^[A-Z][A-Z0-9_]+$/.test(s.id) && s.body.includes('```mermaid')) b.diagrams[s.id] = { document: name, hash: sectionHash(s.body) };
  }
  return { baseline: b, depgraph: graph };
}

/** `문서화` 본체. 모드 판정 → Run 트랜잭션 → 파이프라인 → 검증 → 커밋. */
export async function runPipeline(opts: PipelineOptions): Promise<PipelineSummary> {
  const cfg = opts.cfg ?? loadConfig();
  const paths = opts.paths ?? resolvePaths(cfg);
  const log = opts.log ?? ((m: string) => console.log(m));
  await mkdir(paths.workspace, { recursive: true });
  const recovered = await Run.recover(paths);
  if (recovered !== 'none') log(`이전 실행의 정본 교체를 정리했다: ${recovered}`);

  const baseline = await loadBaseline(paths);
  const changes = await detectChanges(paths, cfg, baseline);
  const currentExists = existsSync(path.join(paths.current, 'features.json'));
  const decision = decideMode(baseline, currentExists, changes, { full: opts.mode === 'full' });
  log(`모드: ${decision.mode} — ${decision.reason}`);
  if (decision.mode === 'UP_TO_DATE' && !opts.phase && !opts.feature) {
    return { status: 'UP_TO_DATE', mode: 'UP_TO_DATE', runName: null, report: formatUpToDate(baseline!), record: null };
  }
  const mode: RunMode = decision.mode === 'INITIAL' ? 'INITIAL' : 'INCREMENTAL';

  // 미완료 Run 재사용/폐기
  let run = await Run.latestIncomplete(paths);
  if (run && (run.state.fingerprint !== changes.fingerprint || run.state.mode !== mode)) {
    await run.abandon(`코드가 다시 바뀌었거나 모드가 달라 새 Run으로 시작 (${run.state.fingerprint.slice(0, 8)} → ${changes.fingerprint.slice(0, 8)})`);
    log(`미완료 ${run.name}을 ABANDONED 처리`);
    run = null;
  }
  if (run) log(`미완료 ${run.name} 재개`);
  else run = await Run.create(paths, mode, changes.fingerprint);
  await atomicWriteJson(path.join(run.dir, 'changes.json'), changes);

  const counting = new CountingRunner(opts.runner ?? new CliClaudeRunner(cfg));
  const ctx: PhaseContext = { cfg, paths, run, runner: counting, scope: { kind: 'ALL' }, log, sessionContext: '' };
  ctx.sessionContext = await readSessionContext(paths, opts.sessionContextPath, run);
  const record = emptyRecord(run, mode, baseline);
  record.changedFiles = changes.files.map((f) => f.file);
  const stopAfter = opts.phase ? PHASE_ORDER.indexOf(opts.phase) : opts.feature ? PHASE_ORDER.indexOf('features') : Number.MAX_SAFE_INTEGER;
  if (opts.phase && stopAfter < 0) throw new Error(`알 수 없는 phase: ${opts.phase} (${PHASE_ORDER.join(', ')})`);
  const reached = (p: string) => PHASE_ORDER.indexOf(p) <= stopAfter;

  try {
    const result = mode === 'INITIAL'
      ? await runInitial(ctx, changes, record, reached, opts.feature)
      : await runIncremental(ctx, baseline!, changes, record, reached, opts.feature);
    if (result.partial) {
      run.state.items['__partial__'] = { status: 'SUCCESS', attempts: 0, costUsd: 0, updatedAt: new Date().toISOString() };
      await run.save();
      const report = `부분 실행 완료 (${opts.phase ?? `feature ${opts.feature}`}) — ${run.name} RUNNING 상태로 남김. 산출물: ${run.staging.current}\n이어서 하려면 \`resume\`.`;
      return { status: 'PARTIAL', mode, runName: run.name, report, record };
    }
    record.costUsd = totalCost(run.state);
    record.claudeCalls = counting.calls;
    if (!result.verification.passed) {
      record.status = 'FAILED';
      record.verification = result.verification;
      record.error = `검증 ${result.verification.iterations}회 후에도 문제가 남음`;
      record.completedAt = new Date().toISOString();
      await run.fail(record.error);
      const report = formatReport(record, { discovered: result.discovered, remainingIssues: result.verification.fixRequired.map((f) => `${f.doc}: ${f.issue}`) });
      await finishRun(run, record, report);
      return { status: 'FAILED', mode, runName: run.name, report, record };
    }
    // 커밋: 문서·baseline·depgraph
    for (const [name, md] of result.docs) await atomicWriteText(path.join(run.staging.docs, name), md);
    const finalDocs = new Map(result.allDocs);
    const { baseline: newBaseline, depgraph } = buildBaseline(baseline, run, result.ws, result.journal, finalDocs, changes, paths.projectRoot, cfg, result.truthText);
    await atomicWriteJson(path.join(run.staging.root, 'baseline.json'), newBaseline);
    await atomicWriteJson(path.join(run.staging.root, 'depgraph.json'), depgraph);
    await run.commit({ deletedDocuments: result.deleted });
    record.status = 'SUCCESS';
    record.baselineAfter = newBaseline.baselineCommit;
    record.verification = result.verification;
    record.updatedDocuments = [...result.docs.keys()].sort();
    record.deletedDocuments = result.deleted;
    record.completedAt = new Date().toISOString();
    const report = formatReport(record, {
      discovered: result.discovered, diagramCounts: diagramCounts(finalDocs), documentsTotal: finalDocs.size, featuresTotal: result.ws.features.features.filter((f) => f.status !== 'REMOVED').length,
      unknowns: countUnknowns(result.ws), issues: countIssues(result.ws), projectName: result.ws.inventory.project.name,
    });
    await finishRun(run, record, report);
    return { status: 'SUCCESS', mode, runName: run.name, report, record };
  } catch (e) {
    const msg = (e as Error).message;
    record.status = 'FAILED';
    record.error = msg;
    record.costUsd = totalCost(run.state);
    record.claudeCalls = counting.calls;
    record.completedAt = new Date().toISOString();
    await run.fail(msg);
    const report = formatReport(record, { discovered: { newApis: 0, dataModelChanges: 0, failures: 0, techDebt: 0 } });
    await finishRun(run, record, report);
    log(`실패: ${msg}`);
    return { status: 'FAILED', mode, runName: run.name, report, record };
  }
}

async function finishRun(run: Run, record: RunRecord, report: string): Promise<void> {
  await atomicWriteJson(path.join(run.dir, 'run.json'), record);
  await atomicWriteText(path.join(run.dir, 'report.txt'), report + '\n');
}

function countUnknowns(ws: CurrentWorkspace): number {
  return ws.inventory.unknowns.length + ws.architecture.unknowns.length + ws.features.unknowns.length + ws.data.unknowns.length + ws.api.unknowns.length + ws.failures.unknowns.length + [...ws.featureAnalyses.values()].reduce((s, fa) => s + fa.unknowns.length, 0);
}

function countIssues(ws: CurrentWorkspace): { confirmed: number; potential: number; improvement: number } {
  const all = [ws.operations.errorHandling, ws.operations.security, ws.operations.performance, ws.operations.techDebt].flatMap((a) => a.items);
  return { confirmed: all.filter((i) => i.classification === 'CONFIRMED_ISSUE').length, potential: all.filter((i) => i.classification === 'POTENTIAL_RISK').length, improvement: all.filter((i) => i.classification === 'IMPROVEMENT').length };
}

interface StageResult {
  partial: boolean;
  ws: CurrentWorkspace;
  journal: Journal;
  docs: Map<string, string>;
  allDocs: Map<string, string>;
  deleted: string[];
  verification: Verification;
  discovered: ReportExtras['discovered'];
  truthText: string;
}

const PARTIAL: StageResult = { partial: true } as StageResult;

async function runInitial(ctx: PhaseContext, changes: ChangeSet, record: RunRecord, reached: (p: string) => boolean, onlyFeature: string | undefined): Promise<StageResult> {
  const { paths, cfg, run } = ctx;
  const inventory = await runInventory(ctx);
  if (!reached('architecture')) return PARTIAL;
  const architecture = await runArchitecture(ctx, inventory, null, null);
  if (!reached('discovery')) return PARTIAL;
  const features = await runDiscovery(ctx, inventory, architecture);
  if (!reached('features')) return PARTIAL;
  const targets = onlyFeature ? features.features.filter((f) => f.id === onlyFeature) : features.features;
  if (onlyFeature && !targets.length) throw new Error(`기능 ${onlyFeature}이(가) features.json에 없다`);
  const { analyses, failed } = await runFeatures(ctx, targets, architecture, new Map(), null, features.features);
  if (failed.length) throw new Error(`기능 분석 실패 ${failed.length}건: ${failed.map((f) => `${f.id}(${f.error.slice(0, 80)})`).join('; ')}`);
  for (const f of features.features) if (analyses.has(f.id)) f.analysisStatus = 'SUCCESS';
  await writeStaging(ctx, 'features.json', features);
  record.affectedFeatures = [...analyses.keys()];
  if (onlyFeature || !reached('data')) return PARTIAL;
  await runData(ctx, features.features);
  if (!reached('api')) return PARTIAL;
  await runApi(ctx, features.features);
  if (!reached('failures')) return PARTIAL;
  const extract = await extractGitHistory(paths.projectRoot, cfg, null, true);
  const failures = await runFailuresInitial(ctx, extract);
  record.failuresDiscovered = failures.failures.map((f) => f.id);
  const journal = await seedJournalFromFailures(ctx, failures, features.features.length);
  if (!reached('operations')) return PARTIAL;
  await runOperations(ctx, analyses, extract);
  const ws = await loadWorkspace(run.staging.current);
  if (!reached('docs')) return PARTIAL;
  const truth = truthLists(paths.projectRoot, cfg);
  const truthText = formatTruthLists(truth);
  ctx.scope = { kind: 'ALL' };
  const docsResult = await updateDocs(ctx, { ws, journal, scope: ctx.scope, prevDocs: new Map(), baseline: null, changes: null, prevAnalyses: new Map() });
  record.updatedDiagrams = docsResult.diagramDecisions.filter((d) => d.decision === 'NEW' || d.decision === 'UPDATED').map((d) => d.diagram);
  if (!reached('verify')) { for (const [n, md] of docsResult.written) await atomicWriteText(path.join(run.staging.docs, n), md); return PARTIAL; }
  const loop = await verificationLoop(ctx, {
    ws, docs: docsResult.written, changed: null, truth, manualKept: new Map(), maxIterations: cfg.verification.max_iterations,
    fixer: makeFixer(ctx, { ws, journal, scope: ctx.scope, prevDocs: new Map(), baseline: null, changes: null }),
  });
  record.manualEditsOverridden = loop.overridden;
  const ops = ws.operations;
  return {
    partial: false, ws, journal, docs: loop.docs, allDocs: loop.docs, deleted: [], verification: loop.verification, truthText,
    discovered: { newApis: ws.api.endpoints.length, dataModelChanges: ws.data.entities.length, failures: failures.failures.length, techDebt: ops.techDebt.items.length },
  };
}

async function runIncremental(ctx: PhaseContext, baseline: Baseline, changes: ChangeSet, record: RunRecord, reached: (p: string) => boolean, onlyFeature: string | undefined): Promise<StageResult> {
  const { paths, cfg, run } = ctx;
  await seedStagingFromCurrent(ctx);
  const prevWs = await loadWorkspace(run.staging.current);
  const prevDocs = readDocs(paths.docsOut);
  const journal0 = await loadJournal(ctx);

  // I02 분류 → I03 영향 → I04 델타
  const classification: Classification = await classifyChanges(ctx, changes, prevWs.features.features);
  await atomicWriteJson(path.join(run.dir, 'classification.json'), classification);
  const graph = (await readJsonOrNull<ReturnType<typeof buildDepgraph>>(path.join(paths.workspace, 'depgraph.json'))) ?? buildDepgraph(prevWs);
  const impact: Impact = analyzeImpact(changes, classification, graph, prevWs.features.features, prevWs.architecture);
  await atomicWriteJson(path.join(run.dir, 'impact.json'), impact);
  ctx.log(`영향: 기능 ${impact.affectedFeatures.length}개, 문서 ${impact.affectedDocs.length}개, 다이어그램 ${impact.affectedDiagrams.length}개${impact.widenReason.length ? ` (확대: ${impact.widenReason.length}건)` : ''}`);
  const delta: FeatureDelta = await runFeatureDelta(ctx, prevWs.features, changes, classification, impact);
  const features: FeaturesFile = applyFeatureDelta(prevWs.features, delta);
  await writeStaging(ctx, 'features.json', features);
  record.newFeatures = delta.newFeatures.map((f) => f.id);
  record.removedFeatures = delta.removedFeatures.filter((r) => r.disposition === 'REMOVED' || r.disposition === 'MIGRATED').map((r) => r.id);

  // I06 아키텍처(필요 시) — 기능 재분석 전에 컴포넌트 목록을 최신화
  let architecture: Architecture = prevWs.architecture;
  if (impact.needsArchitecture) architecture = await runArchitecture(ctx, prevWs.inventory, prevWs.architecture, changes);
  const inventory: Inventory = prevWs.inventory;

  // I05 대상 기능 재분석
  const targetIds = new Set([...delta.changedFeatureIds, ...delta.newFeatures.map((f) => f.id)]);
  if (onlyFeature) { targetIds.clear(); targetIds.add(onlyFeature); }
  const targets = features.features.filter((f) => targetIds.has(f.id) && f.status !== 'REMOVED');
  const { analyses, failed } = await runFeatures(ctx, targets, architecture, prevWs.featureAnalyses, changes, features.features);
  if (failed.length) throw new Error(`기능 재분석 실패 ${failed.length}건: ${failed.map((f) => `${f.id}(${f.error.slice(0, 80)})`).join('; ')}`);
  for (const f of features.features) if (analyses.has(f.id)) f.analysisStatus = 'SUCCESS';
  await writeStaging(ctx, 'features.json', features);
  record.affectedFeatures = [...targetIds].sort();
  if (onlyFeature || !reached('data')) return PARTIAL;

  // I06 데이터/API
  if (impact.needsData) await runData(ctx, features.features);
  if (impact.needsApi || delta.newFeatures.length) await runApi(ctx, features.features);

  // I07 실패/결정 델타 + 저널
  const extract = await extractGitHistory(paths.projectRoot, cfg, changes.baselineCommit, true);
  const { failures, delta: fdelta } = await runFailuresDelta(ctx, extract, prevWs.failures, changes, classification);
  record.failuresDiscovered = fdelta.newFailures.map((f) => f.id);
  const journalDelta = {
    troubleshooting: fdelta.troubleshooting,
    changelog: [...fdelta.changelog, ...classification.changelogCandidates.filter((c) => !fdelta.changelog.some((x) => x.title === c.title)).map((c) => ({ title: c.title, classification: c.classification, description: c.description, impact: c.impact, relatedDocs: [], commits: [], significance: 'MINOR' as const }))],
  };
  const journal = await appendJournal(ctx, journalDelta);

  // 횡단 분석: 영향 기능이 있거나 관련 분류가 있을 때만
  const classes = new Set(classification.items.flatMap((i) => i.classifications));
  const needsOps = targetIds.size > 0 || ['SECURITY_CHANGE', 'PERFORMANCE_CHANGE', 'ERROR_HANDLING_CHANGE', 'ARCHITECTURE_CHANGE'].some((c) => classes.has(c as never));
  if (needsOps) {
    const merged = new Map(prevWs.featureAnalyses);
    for (const [id, fa] of analyses) merged.set(id, fa);
    await runOperations(ctx, merged, extract);
  }
  const ws = await loadWorkspace(run.staging.current);
  if (!reached('docs')) return PARTIAL;

  // I08 문서 갱신 (PARTIAL scope)
  const truth = truthLists(paths.projectRoot, cfg);
  const truthText = formatTruthLists(truth);
  const scope: Scope = { kind: 'PARTIAL', features: [...targetIds], changedFiles: changes.files.map((f) => f.file), docs: impact.affectedDocs, diagrams: impact.affectedDiagrams };
  ctx.scope = scope;
  const docsResult = await updateDocs(ctx, { ws, journal, scope, prevDocs, baseline, changes, prevAnalyses: prevWs.featureAnalyses });
  record.updatedDiagrams = docsResult.diagramDecisions.filter((d) => d.decision === 'NEW' || d.decision === 'UPDATED').map((d) => d.diagram);
  record.unchangedDiagrams = docsResult.diagramDecisions.filter((d) => d.decision === 'UNCHANGED' || d.decision === 'MANUAL_KEPT').map((d) => d.diagram);
  const allDocs = new Map(prevDocs);
  for (const d of docsResult.deleted) allDocs.delete(d);
  for (const [n, md] of docsResult.written) allDocs.set(n, md);
  if (!reached('verify')) { for (const [n, md] of docsResult.written) await atomicWriteText(path.join(run.staging.docs, n), md); return PARTIAL; }

  // I09·I10 검증 루프
  const manualKept = new Map<string, Set<string>>();
  for (const m of docsResult.manualEdits) if (m.kept) manualKept.set(m.document, new Set([...(manualKept.get(m.document) ?? []), m.section]));
  const loop = await verificationLoop(ctx, {
    ws, docs: allDocs, changed: new Set(docsResult.written.keys()), truth, manualKept, maxIterations: cfg.verification.max_iterations,
    fixer: makeFixer(ctx, { ws, journal, scope, prevDocs, baseline, changes }),
  });
  const overridden: ManualOverride[] = [...loop.overridden];
  record.manualEditsOverridden = overridden;
  const written = new Map<string, string>();
  for (const name of new Set([...docsResult.written.keys(), ...loop.docs.keys()])) {
    const md = loop.docs.get(name);
    if (md !== undefined && md !== prevDocs.get(name)?.replace(/\r\n/g, '\n')) written.set(name, md);
  }
  const newApis = ws.api.endpoints.filter((e) => !prevWs.api.endpoints.some((p) => p.method === e.method && p.path === e.path)).length;
  const dataModelChanges = ws.data.entities.filter((e) => JSON.stringify(e) !== JSON.stringify(prevWs.data.entities.find((p) => p.name === e.name))).length;
  return {
    partial: false, ws, journal, docs: written, allDocs: loop.docs, deleted: docsResult.deleted, verification: loop.verification, truthText,
    discovered: { newApis, dataModelChanges, failures: fdelta.newFailures.length, techDebt: ws.operations.techDebt.items.length - (needsOps ? 0 : ws.operations.techDebt.items.length) },
  };
}

/** `문서화 상태`: LLM 없이 baseline 대비 변경·예상 영향·마지막 Run을 보여 준다. */
export async function statusText(opts: { cfg?: HarnessConfig; paths?: HarnessPaths } = {}): Promise<string> {
  const cfg = opts.cfg ?? loadConfig();
  const paths = opts.paths ?? resolvePaths(cfg);
  const baseline = await loadBaseline(paths);
  const changes = await detectChanges(paths, cfg, baseline, { withHunks: false });
  const currentExists = existsSync(path.join(paths.current, 'features.json'));
  const decision = decideMode(baseline, currentExists, changes, { full: false });
  const graph = await readJsonOrNull<ReturnType<typeof buildDepgraph>>(path.join(paths.workspace, 'depgraph.json'));
  const changedFiles = changes.files.map((f) => `${f.changeType} ${f.file}`);
  const affected = graph ? featuresForFiles(graph, changes.files.map((f) => f.file)) : [];
  const last = await Run.latest(paths);
  const lastRecord = last ? await readJsonOrNull<RunRecord>(path.join(last.dir, 'run.json')) : null;
  return formatStatus({ baseline, currentExists, decision, changedFiles, affectedFeatures: affected, lastRun: lastRecord, docsCount: readDocs(paths.docsOut).size, stale: !!baseline && changes.files.length > 0 });
}

/** `문서화 검증`: 정본 문서를 검증만 하고 아무것도 바꾸지 않는다. */
export async function verifyOnly(opts: { cfg?: HarnessConfig; paths?: HarnessPaths; runner?: ClaudeRunner; log?: (m: string) => void; llm?: boolean } = {}): Promise<{ verification: Verification; report: string }> {
  const cfg = opts.cfg ?? loadConfig();
  const paths = opts.paths ?? resolvePaths(cfg);
  const log = opts.log ?? ((m: string) => console.log(m));
  if (!existsSync(path.join(paths.current, 'features.json'))) throw new Error('워크스페이스 정본이 없다. 먼저 `문서화`를 실행한다.');
  const ws = await loadWorkspace(paths.current);
  const docs = readDocs(paths.docsOut);
  const run = await Run.create(paths, 'VERIFY_ONLY', 'verify');
  const ctx: PhaseContext = { cfg, paths, run, runner: opts.runner ?? new CliClaudeRunner(cfg), scope: { kind: 'ALL' }, log, sessionContext: '' };
  const v = await runVerificationPass(ctx, { ws, docs, changed: null, truth: truthLists(paths.projectRoot, cfg), iteration: 1, manualKept: new Map(), llm: opts.llm });
  run.state.status = 'SUCCESS';
  await run.save();
  await rm(run.staging.root, { recursive: true, force: true }).catch(() => undefined);
  const lines = ['문서화 검증 완료', '', `문서 ${docs.size}개 · 판정: ${v.passed ? '통과' : '문제 있음'}`, '', `- Hallucination: ${v.hallucinations.length}`, `- Missing: ${v.missingItems.length}`, `- Incorrect Relation: ${v.incorrectRelations.length}`, `- Diagram: ${v.diagramIssues.length}`, `- Unsupported Claim: ${v.unsupportedClaims.length}`, `- Mermaid 파서: ${v.mermaidParser}`, ''];
  if (v.fixRequired.length) lines.push('고칠 것:', ...v.fixRequired.slice(0, 60).map((f) => `- ${f.doc}: ${f.issue}`));
  const report = lines.join('\n');
  await atomicWriteJson(path.join(run.dir, 'verification.json'), v);
  await atomicWriteText(path.join(run.dir, 'report.txt'), report + '\n');
  return { verification: v, report };
}

export async function fileTreeText(cfg: HarnessConfig, paths: HarnessPaths): Promise<string> {
  return fileTree(paths.projectRoot, cfg);
}

export { readStaging, callClaude, today };
