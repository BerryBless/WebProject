import type { TruthLists } from '../context.js';
import { formatTruthLists } from '../context.js';
import type { PhaseContext } from '../phases/common.js';
import type { CurrentWorkspace, DiagramIssue, Issue, ManualOverride, Verification } from '../types.js';
import { checkConsistency } from './consistency.js';
import { consistencyLlm, groupDocs, relatedUnchangedDocs, verifyLlm } from './llm.js';
import { checkMermaid } from './mermaid.js';
import { checkReferences } from './references.js';

export interface VerifyOptions {
  ws: CurrentWorkspace;
  /** 검증 대상 전체 문서(정본 + 이번 Run이 갱신한 문서를 합친 상태). */
  docs: Map<string, string>;
  /** 이번 Run이 갱신한 문서 이름. null이면 전체(INITIAL). */
  changed: Set<string> | null;
  truth: TruthLists;
  iteration: number;
  /** 사람이 고쳐 유지한 섹션(document → section ids). 여기 이슈가 나면 override 대상. */
  manualKept: Map<string, Set<string>>;
  llm?: boolean;
}

export const BLOCKING_TYPES = new Set(['HALLUCINATED_PATH', 'UNKNOWN_FEATURE_ID', 'BROKEN_LINK', 'MISSING_ENDPOINT_IN_DOC', 'EXTRA_ENDPOINT_IN_DOC', 'MISSING_ENTITY_IN_DOC', 'MISSING_COMPONENT_IN_DOC', 'MISSING_ENDPOINT', 'MISSING_ENTITY']);
const BLOCKING_DIAGRAM = new Set<DiagramIssue['type']>(['INCORRECT_DIAGRAM_RELATION', 'DIAGRAM_NODE_NOT_IN_CODE', 'MERMAID_SYNTAX_ERROR', 'MERMAID_UNCLOSED_BLOCK', 'MERMAID_STYLE_FORBIDDEN', 'SECTION_ANCHOR_MISMATCH']);

/** 한 번의 검증 패스: 결정적 검사(전체 문서) + LLM 검증(변경 문서군) + LLM 일관성(증분). */
export async function runVerificationPass(ctx: PhaseContext, opts: VerifyOptions): Promise<Verification> {
  const { ws, docs, changed, truth, iteration } = opts;
  const refs = checkReferences(docs, ctx.paths.projectRoot, ws, ctx.cfg);
  const mermaid = await checkMermaid(docs, ws, ctx.cfg, ctx.paths.projectRoot);
  const cons = checkConsistency(docs, ws, truth);
  const v: Verification = {
    score: { coverage: 0, accuracy: 0 }, hallucinations: [], missingItems: [], incorrectRelations: [], diagramIssues: mermaid.issues, unsupportedClaims: [], fixRequired: [],
    mermaidParser: mermaid.parser, iterations: iteration, passed: false, warnings: [...mermaid.warnings, ...cons.warnings, ...refs.warnings],
  };
  for (const i of mermaid.issues) i.source = 'deterministic';
  for (const i of refs.issues) { i.source = 'deterministic'; (i.type === 'BROKEN_LINK' ? v.incorrectRelations : v.hallucinations).push(i); }
  for (const i of cons.issues) { i.source = 'deterministic'; (i.type.startsWith('MISSING') ? v.missingItems : v.incorrectRelations).push(i); }

  if (opts.llm !== false && ctx.cfg.verification.enabled) {
    const truthText = formatTruthLists(truth);
    const groups = groupDocs(docs, changed, ctx.cfg.verification.feature_chunk_docs ?? 0);
    const scores: { coverage: number; accuracy: number }[] = [];
    for (const g of groups) {
      const part = await verifyLlm(ctx, g, ws, truthText, [...docs.keys()], iteration);
      scores.push(part.score);
      v.hallucinations.push(...part.hallucinations);
      v.missingItems.push(...part.missingItems);
      v.incorrectRelations.push(...part.incorrectRelations);
      v.diagramIssues.push(...part.diagramIssues);
      v.unsupportedClaims.push(...part.unsupportedClaims);
      v.fixRequired.push(...part.fixRequired);
    }
    if (scores.length) v.score = { coverage: Math.round(scores.reduce((s, x) => s + x.coverage, 0) / scores.length), accuracy: Math.round(scores.reduce((s, x) => s + x.accuracy, 0) / scores.length) };
    if (changed) {
      const changedDocs = new Map([...docs].filter(([n]) => changed.has(n)));
      const issues = await consistencyLlm(ctx, changedDocs, relatedUnchangedDocs(changedDocs, docs), iteration);
      v.incorrectRelations.push(...issues);
    }
  }
  // 결정적·LLM 이슈를 fixRequired로 정규화(문서·섹션 단위)
  const seen = new Set(v.fixRequired.map((f) => `${f.doc}|${f.section ?? ''}|${f.issue}`));
  const addFix = (i: Issue, prefix: string) => {
    const key = `${i.document}|${i.section ?? ''}|${i.description}`;
    if (seen.has(key)) return;
    seen.add(key);
    v.fixRequired.push({ doc: i.document, section: i.section, issue: `[${prefix}:${i.type}] ${i.description}${i.section ? ` section=${i.section}` : ''}`, evidence: i.evidence });
  };
  for (const i of v.hallucinations) addFix(i, 'hallucination');
  for (const i of v.missingItems) addFix(i, 'missing');
  for (const i of v.incorrectRelations) addFix(i, 'relation');
  for (const i of v.diagramIssues) addFix({ ...i, section: i.section ?? (/^[A-Z][A-Z0-9_]+$/.test(i.diagram) ? i.diagram : undefined) }, 'diagram');
  for (const i of v.unsupportedClaims) addFix(i, 'unsupported');
  v.passed = !hasBlocking(v);
  return v;
}

/** 결정적 검사의 차단 이슈만 남았는지(경로·링크·문법·집합 대조). 이것이 0이면 잔여 LLM 지적을 안고 발행할 수 있다. */
export function hasDeterministicBlocking(v: Verification): boolean {
  const det = (xs: Issue[]) => xs.filter((i) => i.source === 'deterministic');
  if (det(v.hallucinations).length) return true;
  if (det(v.incorrectRelations).length) return true;
  if (v.diagramIssues.some((i) => i.source === 'deterministic' && BLOCKING_DIAGRAM.has(i.type))) return true;
  if (det(v.missingItems).some((i) => BLOCKING_TYPES.has(i.type))) return true;
  return false;
}

export function hasBlocking(v: Verification): boolean {
  if (v.hallucinations.some((i) => i.type !== 'ENDPOINT_NOT_IN_FEATURE_DOCS')) return true;
  if (v.incorrectRelations.length) return true;
  if (v.diagramIssues.some((i) => BLOCKING_DIAGRAM.has(i.type))) return true;
  if (v.missingItems.some((i) => BLOCKING_TYPES.has(i.type))) return true;
  return false;
}

export function issuesByDoc(v: Verification): Map<string, string[]> {
  const out = new Map<string, string[]>();
  for (const f of v.fixRequired) out.set(f.doc, [...(out.get(f.doc) ?? []), f.issue]);
  return out;
}

export interface LoopOptions extends Omit<VerifyOptions, 'iteration'> {
  maxIterations: number;
  /** 문서별 이슈를 받아 고친 문서를 돌려준다(갱신된 문서만). */
  fixer: (issues: Map<string, string[]>, iteration: number) => Promise<{ docs: Map<string, string>; overridden: ManualOverride[] }>;
}

export interface LoopResult { verification: Verification; docs: Map<string, string>; iterations: number; passed: boolean; overridden: ManualOverride[]; history: Verification[] }

/**
 * 검증 → 문제 → 수정 → 재검증(스펙 §8). 최대 maxIterations번 검증한다. 코드는 절대 건드리지 않는다.
 * 수동 수정 섹션에 이슈가 나면 fixer가 덮어쓰고 overridden에 기록한다.
 */
/** 결정적 차단 이슈만 문서별로 모은다(비용 0 사전 루프용 — 경고·비차단 유형은 제외). */
export function deterministicBlockingByDoc(v: Verification): Map<string, string[]> {
  const out = new Map<string, string[]>();
  const add = (i: Issue, prefix: string) => out.set(i.document, [...(out.get(i.document) ?? []), `[${prefix}:${i.type}] ${i.description}${i.section ? ` section=${i.section}` : ''}`]);
  for (const i of v.hallucinations) if (i.source === 'deterministic') add(i, 'hallucination');
  for (const i of v.incorrectRelations) if (i.source === 'deterministic') add(i, 'relation');
  for (const i of v.missingItems) if (i.source === 'deterministic' && BLOCKING_TYPES.has(i.type)) add(i, 'missing');
  for (const i of v.diagramIssues) if (i.source === 'deterministic' && BLOCKING_DIAGRAM.has(i.type)) add({ ...i, section: i.section ?? (/^[A-Z][A-Z0-9_]+$/.test(i.diagram) ? i.diagram : undefined) }, 'diagram');
  return out;
}

export async function verificationLoop(ctx: PhaseContext, opts: LoopOptions): Promise<LoopResult> {
  let docs = new Map(opts.docs);
  let changed = opts.changed ? new Set(opts.changed) : null;
  const overridden: ManualOverride[] = [];
  const history: Verification[] = [];
  let v: Verification | null = null;

  // 사전 루프(비용 0): 결정적 차단 이슈(없는 경로·깨진 링크·문법·집합 대조)를 먼저 잡아 해당 문서만 고친다.
  // LLM 검증(전체 문서군, 회차당 수십 달러)은 그 뒤에 한 번씩만 돈다(2026-09-25: 사용량 한도 창 안에 끝내기 위한 순서).
  if (opts.llm !== false) {
    for (let pre = 1; pre <= 3; pre++) {
      const det = await runVerificationPass(ctx, { ...opts, docs, changed, iteration: pre, llm: false });
      const byDoc = deterministicBlockingByDoc(det);
      if (!byDoc.size) break;
      ctx.log(`결정적 사전 검사 ${pre}/3: 차단 이슈 ${[...byDoc.values()].reduce((s, x) => s + x.length, 0)}건 (문서 ${byDoc.size}개) → 해당 문서만 수정`);
      const fixed = await opts.fixer(byDoc, 100 + pre);
      overridden.push(...fixed.overridden);
      for (const [name, md] of fixed.docs) { docs.set(name, md); changed?.add(name); }
    }
  }
  for (let iteration = 1; iteration <= Math.max(1, opts.maxIterations); iteration++) {
    v = await runVerificationPass(ctx, { ...opts, docs, changed, iteration });
    history.push(v);
    ctx.log(`검증 ${iteration}/${opts.maxIterations}: 환각 ${v.hallucinations.length} · 누락 ${v.missingItems.length} · 관계 ${v.incorrectRelations.length} · 다이어그램 ${v.diagramIssues.length} · 근거없음 ${v.unsupportedClaims.length} · 경고 ${v.warnings?.length ?? 0} → ${v.passed ? '통과' : '수정 필요'}`);
    if (v.passed || iteration === opts.maxIterations) break;
    const byDoc = issuesByDoc(v);
    for (const f of v.fixRequired) {
      const kept = opts.manualKept.get(f.doc);
      if (f.section && kept?.has(f.section)) overridden.push({ document: f.doc, section: f.section, reason: f.issue });
    }
    const fixed = await opts.fixer(byDoc, iteration);
    overridden.push(...fixed.overridden);
    // 다음 회차의 LLM 검증은 이번에 고친 문서와 이슈가 있던 문서만 본다(나머지는 이미 검증됐다). 결정적 검사는 늘 전체다.
    changed = new Set([...fixed.docs.keys(), ...byDoc.keys()]);
    for (const [name, md] of fixed.docs) docs.set(name, md);
  }
  // 사후 루프(비용 0에 가까움): 마지막 수정 회차가 새 결정적 차단 이슈(없는 경로 등)를 만들었으면 그 문서만 싸게 고친다.
  // LLM 지적은 이미 판정된 것을 유지하고, 결정적 항목만 다시 계산한다(2026-09-25 실측: 수정본이 없는 경로 1건을 넣어 발행 실패).
  if (opts.llm !== false && hasDeterministicBlocking(v!)) {
    for (let post = 1; post <= 3; post++) {
      const det = await runVerificationPass(ctx, { ...opts, docs, changed, iteration: 200 + post, llm: false });
      const byDoc = deterministicBlockingByDoc(det);
      const stripDet = <T extends Issue>(xs: T[]) => xs.filter((i) => i.source !== 'deterministic');
      v = { ...v!, hallucinations: [...stripDet(v!.hallucinations), ...det.hallucinations], missingItems: [...stripDet(v!.missingItems), ...det.missingItems], incorrectRelations: [...stripDet(v!.incorrectRelations), ...det.incorrectRelations], diagramIssues: [...stripDet(v!.diagramIssues), ...det.diagramIssues], warnings: det.warnings };
      if (!byDoc.size) break;
      ctx.log(`결정적 사후 검사 ${post}/3: 차단 이슈 ${[...byDoc.values()].reduce((s, x) => s + x.length, 0)}건 → 해당 문서만 수정`);
      const fixed = await opts.fixer(byDoc, 200 + post);
      overridden.push(...fixed.overridden);
      for (const [name, md] of fixed.docs) docs.set(name, md);
    }
    v!.passed = !hasBlocking(v!);
  }
  return { verification: v!, docs, iterations: history.length, passed: v!.passed, overridden, history };
}
