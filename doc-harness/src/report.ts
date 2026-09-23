import type { Decision } from './mode.js';
import type { Baseline, RunRecord, Verification } from './types.js';

function verificationLines(v: Verification | null): string[] {
  if (!v) return ['Verification: (실행되지 않음)'];
  return [
    'Verification:',
    `- Hallucination: ${v.hallucinations.length}`,
    `- Missing: ${v.missingItems.length}`,
    `- Incorrect Relation: ${v.incorrectRelations.length}`,
    `- Diagram: ${v.diagramIssues.length}`,
    `- Unsupported Claim: ${v.unsupportedClaims.length}`,
    `- 반복: ${v.iterations}회 · Mermaid 파서: ${v.mermaidParser} · 점수(내부 지표) coverage ${v.score.coverage} / accuracy ${v.score.accuracy}`,
  ];
}

export interface ReportExtras {
  discovered: { newApis: number; dataModelChanges: number; failures: number; techDebt: number };
  diagramCounts?: Record<string, number>;
  documentsTotal?: number;
  featuresTotal?: number;
  unknowns?: number;
  issues?: { confirmed: number; potential: number; improvement: number };
  projectName?: string;
  remainingIssues?: string[];
}

export function formatUpToDate(baseline: Baseline): string {
  return ['문서화 확인 완료', '', '마지막 문서화 이후 코드 변경사항이 없습니다.', '현재 문서는 최신 상태입니다.', '', `기준: ${baseline.baselineCommit.slice(0, 10)} · 마지막 실행 ${baseline.lastSuccessfulRun} (${baseline.lastSuccessfulAt})`].join('\n');
}

export function formatReport(r: RunRecord, x: ReportExtras): string {
  const lines: string[] = [];
  if (r.status !== 'SUCCESS') {
    lines.push('문서화 실패', '', `모드: ${r.mode}`, `실행: ${r.name}`, `원인: ${r.error ?? '알 수 없음'}`, '', 'Baseline과 기존 문서는 그대로 유지되었습니다. 스테이징 산출물은 workspace/runs/' + r.name + '/staging 에 남아 있습니다.', '');
    if (x.remainingIssues?.length) lines.push('남은 문제:', ...x.remainingIssues.slice(0, 40).map((i) => `- ${i}`), '');
    lines.push(...verificationLines(r.verification));
    return lines.join('\n');
  }
  if (r.mode === 'INITIAL') {
    lines.push('Documentation Complete', '', `Project: ${x.projectName ?? '-'}`, `Commit: ${(r.baselineAfter ?? '').slice(0, 10)}`, '', `Features discovered: ${x.featuresTotal ?? r.affectedFeatures.length}`, `Features analyzed: ${r.affectedFeatures.length}`, '', `Documents generated: ${r.updatedDocuments.length}`, '');
    if (x.diagramCounts) {
      lines.push('Diagrams:');
      for (const [k, v] of Object.entries(x.diagramCounts)) lines.push(`  ${k}: ${v}`);
      lines.push('');
    }
    lines.push(`Failures discovered: ${r.failuresDiscovered.length}`, '');
    if (x.issues) lines.push('Issues:', `  Confirmed: ${x.issues.confirmed}`, `  Potential: ${x.issues.potential}`, `  Improvement: ${x.issues.improvement}`, '');
    lines.push(...verificationLines(r.verification), '', `Unknowns: ${x.unknowns ?? 0}`, '', 'Documentation:', 'docs/generated/README.md', '', `비용: $${r.costUsd.toFixed(2)} · Claude 호출 ${r.claudeCalls}회 · Baseline 생성 완료`);
    return lines.join('\n');
  }
  lines.push('문서화 완료', '', `모드: ${r.mode}`, `기준: ${(r.baselineBefore ?? '').slice(0, 10)} → ${(r.baselineAfter ?? '').slice(0, 10)}`, '',
    `변경 파일: ${r.changedFiles.length}`, `영향받은 기능: ${r.affectedFeatures.length}`, `신규 기능: ${r.newFeatures.length}`, `삭제 기능: ${r.removedFeatures.length}`, '',
    `문서 수정: ${r.updatedDocuments.length}${r.deletedDocuments.length ? ` (삭제 ${r.deletedDocuments.length})` : ''}`, `Diagram 수정: ${r.updatedDiagrams.length} (유지 ${r.unchangedDiagrams.length})`, '',
    '발견:', `- 신규 API: ${x.discovered.newApis}`, `- Data Model 변경: ${x.discovered.dataModelChanges}`, `- 실패/Workaround: ${x.discovered.failures}`, `- Technical Debt: ${x.discovered.techDebt}`, '');
  if (r.manualEditsOverridden.length) lines.push('수동 수정 교체:', ...r.manualEditsOverridden.map((m) => `- ${m.document} #${m.section}: ${m.reason.slice(0, 120)}`), '');
  lines.push(...verificationLines(r.verification), '', `Baseline 갱신 완료 · 비용 $${r.costUsd.toFixed(2)} · Claude 호출 ${r.claudeCalls}회`);
  return lines.join('\n');
}

export function formatStatus(s: { baseline: Baseline | null; currentExists: boolean; decision: { mode: Decision; reason: string }; changedFiles: string[]; affectedFeatures: string[]; lastRun: RunRecord | null; docsCount: number; stale: boolean }): string {
  const lines: string[] = ['문서화 상태', ''];
  if (!s.baseline) lines.push('Baseline: 없음 (최초 실행 필요 — `문서화`)');
  else lines.push(`Baseline: 버전 ${s.baseline.documentationVersion} · 커밋 ${s.baseline.baselineCommit.slice(0, 10)} · 마지막 실행 ${s.baseline.lastSuccessfulRun} (${s.baseline.lastSuccessfulAt})`);
  lines.push(`생성 문서: ${s.docsCount}개`, `다음 실행 모드: ${s.decision.mode} — ${s.decision.reason}`);
  if (s.stale) lines.push('', 'DOCUMENTATION MAY BE STALE');
  if (s.changedFiles.length) {
    lines.push('', `변경 파일 ${s.changedFiles.length}개:`, ...s.changedFiles.slice(0, 30).map((f) => `- ${f}`));
    if (s.changedFiles.length > 30) lines.push(`- … ${s.changedFiles.length - 30}개 더`);
    lines.push('', `예상 영향 기능: ${s.affectedFeatures.join(', ') || '(없음 — 신규 기능 탐지 필요할 수 있음)'}`);
  }
  if (s.lastRun) lines.push('', `마지막 Run: ${s.lastRun.name} ${s.lastRun.mode} ${s.lastRun.status} (${s.lastRun.completedAt || s.lastRun.startedAt})${s.lastRun.error ? ` — ${s.lastRun.error}` : ''}`);
  return lines.join('\n');
}
