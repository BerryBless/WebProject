import { existsSync } from 'node:fs';
import path from 'node:path';
import { isQuotaError } from '../claude.js';
import { pathList } from '../context.js';
import { hashFile, today } from '../fsx.js';
import { buildPrompt } from '../prompts.js';
import type { Architecture, ChangeSet, FeatureAnalysis, FeatureSummary } from '../types.js';
import { callClaude, sessionContextBlock, type PhaseContext } from './common.js';

export { today } from '../fsx.js';

function relatedFilesHash(root: string, files: string[]): string {
  return files.map((f) => {
    const abs = path.join(root, f);
    return `${f}:${existsSync(abs) ? hashFile(abs) : 'missing'}`;
  }).join('|');
}

function changeHintsFor(feature: FeatureSummary, changes: ChangeSet | null): string {
  if (!changes) return '(전체 분석)';
  const related = new Set(feature.relatedFiles);
  const hits = changes.files.filter((f) => related.has(f.file) || (f.oldPath && related.has(f.oldPath)));
  if (!hits.length) return '(이 기능의 파일에 직접 변경 없음 — 의존 기능·컴포넌트 변경으로 재검증 대상이 됨)';
  return hits.map((f) => {
    const added = f.addedLines.slice(0, 60).map((l) => `+ ${l}`).join('\n');
    const removed = f.removedLines.slice(0, 60).map((l) => `- ${l}`).join('\n');
    return `### ${f.changeType} ${f.file}${f.oldPath ? ` (from ${f.oldPath})` : ''}\n\`\`\`diff\n${removed}\n${added}\n\`\`\``;
  }).join('\n\n');
}

/** LLM 출력의 형식 보정: id·상태·다이어그램 id 접두사·이전 history 보존. */
export function normalizeFeatureAnalysis(fa: FeatureAnalysis, summary: FeatureSummary, previous: FeatureAnalysis | null): FeatureAnalysis {
  fa.feature.id = summary.id;
  fa.feature.slug = fa.feature.slug || summary.slug;
  fa.feature.analysisStatus = 'SUCCESS';
  for (const d of fa.diagrams) {
    if (!d.id.startsWith(summary.id)) d.id = `${summary.id}_${d.id.replace(/^[A-Z0-9]+_/, '')}`;
  }
  if (previous?.history?.length) {
    const key = (h: { date: string; title: string }) => `${h.date}|${h.title}`;
    const seen = new Set(fa.history.map(key));
    fa.history = [...previous.history.filter((h) => !seen.has(key(h))), ...fa.history];
  }
  return fa;
}

export interface FixHint { issues: string[]; attempt: number }

export function fixTail(fix?: FixHint): { tail?: string; suffix: string } {
  if (!fix?.issues.length) return { suffix: '' };
  return {
    tail: `## 검증에서 지적된 문제(반드시 코드로 확인해 고친다. 지적이 틀렸다면 근거를 unknowns에 남기고 원래대로 둔다)\n\n${fix.issues.map((i) => `- ${i}`).join('\n')}`,
    suffix: `:fix${fix.attempt}`,
  };
}

/** 기능 분석 프롬프트와 입력 해시 재료. 해시 재계산 스크립트도 이 함수를 써야 런타임과 같은 값을 얻는다. */
export function buildFeaturePrompt(ctx: Pick<PhaseContext, 'cfg' | 'paths' | 'sessionContext'>, summary: FeatureSummary, architecture: Architecture, previous: FeatureAnalysis | null, changes: ChangeSet | null, fix: FixHint | undefined, featureIndex: FeatureSummary[]): { prompt: string; suffix: string; extraHash: string } {
  const { tail, suffix } = fixTail(fix);
  const prompt = buildPrompt('04_feature_analysis', {
    featureId: summary.id,
    featureName: summary.name,
    today: today(),
    featureIndex: featureIndex.filter((f) => f.status !== 'REMOVED').map((f) => `- ${f.id} ${f.name} (${f.slug})`).join('\n') || '(없음)',
    // analysisStatus는 실행 중 바뀌는 값이라 프롬프트(=입력 해시)에서 뺀다. 그대로 두면 resume마다 캐시가 깨져 전 기능을 다시 분석한다(2026-09-24 실측).
    feature: JSON.stringify({ ...summary, analysisStatus: 'PENDING' }, null, 1),
    relatedFiles: pathList(summary.relatedFiles, ctx.cfg) || '(없음 — Glob/Grep으로 찾는다)',
    components: JSON.stringify(architecture.components.map((c) => ({ id: c.id, name: c.name, path: c.path })), null, 1),
    previous: previous ? JSON.stringify({ ...previous, diagrams: previous.diagrams.map((d) => ({ id: d.id, type: d.type, title: d.title, mermaid: d.mermaid })) }, null, 1).slice(0, 50_000) : '(없음 — 최초 분석)',
    changeHints: changeHintsFor(summary, changes),
    sessionContext: sessionContextBlock(ctx),
  }, { tail });
  return { prompt, suffix, extraHash: relatedFilesHash(ctx.paths.projectRoot, summary.relatedFiles) };
}

export async function analyzeFeature(ctx: PhaseContext, summary: FeatureSummary, architecture: Architecture, previous: FeatureAnalysis | null, changes: ChangeSet | null, fix?: FixHint, featureIndex: FeatureSummary[] = [summary]): Promise<FeatureAnalysis> {
  const { prompt, suffix, extraHash } = buildFeaturePrompt(ctx, summary, architecture, previous, changes, fix, featureIndex);
  const out = await callClaude<FeatureAnalysis>(ctx, { itemId: `feature:${summary.id}${suffix}`, phase: 'feature', schemaName: 'feature', prompt, outFile: `features/${summary.id}.json`, extraHash });
  return normalizeFeatureAnalysis(out, summary, previous);
}

/** 기능 목록을 워커 풀로 분석한다. 하나가 실패해도 나머지는 계속하고, 끝에 실패 목록을 던진다. */
export async function runFeatures(ctx: PhaseContext, features: FeatureSummary[], architecture: Architecture, previous: Map<string, FeatureAnalysis>, changes: ChangeSet | null, featureIndex: FeatureSummary[] = features): Promise<{ analyses: Map<string, FeatureAnalysis>; failed: { id: string; error: string }[] }> {
  const analyses = new Map<string, FeatureAnalysis>();
  const failed: { id: string; error: string }[] = [];
  const queue = [...features];
  const parallelism = Math.max(1, ctx.cfg.analysis.feature_parallelism);
  let aborted = false;
  const worker = async () => {
    for (;;) {
      if (aborted) return;
      const f = queue.shift();
      if (!f) return;
      try {
        analyses.set(f.id, await analyzeFeature(ctx, f, architecture, previous.get(f.id) ?? null, changes, undefined, featureIndex));
      } catch (e) {
        const message = (e as Error).message;
        failed.push({ id: f.id, error: message });
        // 사용량 한도면 남은 기능을 시도하지 않는다(전부 같은 이유로 실패한다). 미시작 항목은 PENDING으로 남아 resume 대상이 된다.
        if (isQuotaError(message)) { aborted = true; ctx.log(`사용량 한도 감지 — 남은 기능 ${queue.length}개는 시도하지 않고 중단한다. 한도가 풀리면 resume.`); }
      }
    }
  };
  await Promise.all(Array.from({ length: Math.min(parallelism, queue.length || 1) }, worker));
  return { analyses, failed };
}
