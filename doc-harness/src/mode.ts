import type { Baseline, ChangeSet } from './types.js';

export type Decision = 'INITIAL' | 'INCREMENTAL' | 'UP_TO_DATE';

/**
 * 스펙 §6.3. INITIAL: baseline/current 없음, baseline 커밋이 저장소에 없음, 또는 --full.
 * UP_TO_DATE: baseline이 있고 변경 파일 0. 그 외 INCREMENTAL.
 */
export function decideMode(baseline: Baseline | null, currentExists: boolean, changes: ChangeSet, opts: { full: boolean }): { mode: Decision; reason: string } {
  if (opts.full) return { mode: 'INITIAL', reason: '--full 지정' };
  if (!baseline) return { mode: 'INITIAL', reason: 'baseline.json 없음(최초 실행)' };
  if (!currentExists) return { mode: 'INITIAL', reason: 'workspace/current 없음' };
  if (!baseline.baselineCommit || changes.baselineCommit === null) return { mode: 'INITIAL', reason: `baseline 커밋 ${baseline.baselineCommit.slice(0, 10) || '(없음)'}이 저장소에 없다` };
  if (changes.files.length === 0) return { mode: 'UP_TO_DATE', reason: '마지막 문서화 이후 변경 없음' };
  return { mode: 'INCREMENTAL', reason: `변경 파일 ${changes.files.length}개` };
}
