import { pathList } from '../context.js';
import { listSourceFiles } from '../fsx.js';
import { formatGitExtract, formatMarkers, type GitExtract } from '../git-extract.js';
import { buildPrompt } from '../prompts.js';
import type { ChangeSet, Classification, FailureRecord, FailuresFile, Significance, Status } from '../types.js';
import { callClaude, readStaging, sessionContextBlock, writeStaging, type PhaseContext } from './common.js';
import { today } from './feature.js';

/** 문서로 누적되는 실행 기록(12_TROUBLESHOOTING·20_CHANGELOG의 원천). current/journal.json */
export interface Journal {
  troubleshooting: { id: string; symptom: string; cause: string; howToConfirm: string[]; resolution: string[]; relatedCode: string[]; status: Status; addedAt: string }[];
  changelog: { date: string; run: string; title: string; classification: string[]; description: string; impact: string[]; relatedDocs: string[]; commits: string[]; significance: Significance }[];
}

export interface FailureDelta {
  newFailures: FailureRecord[];
  updatedFailures: FailureRecord[];
  troubleshooting: Omit<Journal['troubleshooting'][number], 'addedAt'>[];
  changelog: Omit<Journal['changelog'][number], 'date' | 'run'>[];
  unknowns: string[];
}

export function docPaths(files: string[]): string[] {
  return files.filter((f) => f.endsWith('.md') && (f.startsWith('plan/') || f.startsWith('docs/') || f === 'README.md'));
}

export function nextFailureId(existing: FailureRecord[]): string {
  const max = existing.reduce((m, f) => Math.max(m, Number(f.id.replace('FAIL', '')) || 0), 0);
  return `FAIL${String(max + 1).padStart(3, '0')}`;
}

export async function runFailuresInitial(ctx: PhaseContext, extract: GitExtract): Promise<FailuresFile> {
  const files = listSourceFiles(ctx.paths.projectRoot, ctx.cfg);
  const prompt = buildPrompt('06_failure_history', {
    today: today(),
    gitHistory: formatGitExtract(extract),
    markers: formatMarkers(extract.markers),
    docPaths: pathList(docPaths(files), ctx.cfg) || '(없음)',
    sessionContext: sessionContextBlock(ctx),
  });
  const out = await callClaude<FailuresFile>(ctx, { itemId: 'failures', phase: 'failures', schemaName: 'failures', prompt, outFile: 'failures.json' });
  out.failures.sort((a, b) => a.id.localeCompare(b.id));
  return out;
}

export async function runFailuresDelta(ctx: PhaseContext, extract: GitExtract, previous: FailuresFile, changes: ChangeSet, classification: Classification): Promise<{ failures: FailuresFile; delta: FailureDelta }> {
  const changedSet = new Set(changes.files.map((f) => f.file));
  const prompt = buildPrompt('I07_failure_delta', {
    today: today(),
    nextFailureId: nextFailureId(previous.failures),
    existingFailures: previous.failures.map((f) => `- ${f.id} ${f.title} (${f.causeConfidence})`).join('\n') || '(없음)',
    classification: [classification.summary, ...classification.items.map((i) => `- ${i.file}: ${i.classifications.join('/')} [${i.significance}]`)].join('\n'),
    changes: changes.files.filter((f) => f.requiresAnalysis).slice(0, 40).map((f) => `### ${f.changeType} ${f.file}\n\`\`\`diff\n${f.removedLines.slice(0, 40).map((l) => `- ${l}`).join('\n')}\n${f.addedLines.slice(0, 40).map((l) => `+ ${l}`).join('\n')}\n\`\`\``).join('\n\n') || '(분석 대상 변경 없음)',
    gitHistory: formatGitExtract(extract),
    markers: formatMarkers(extract.markers, changedSet),
    sessionContext: sessionContextBlock(ctx),
  });
  const delta = await callClaude<FailureDelta>(ctx, { itemId: 'failures-delta', phase: 'failures', schemaName: 'failure_delta', prompt, outFile: 'failure_delta.json' });
  const byId = new Map(previous.failures.map((f) => [f.id, f]));
  for (const u of delta.updatedFailures) if (byId.has(u.id)) byId.set(u.id, u);
  for (const n of delta.newFailures) if (!byId.has(n.id)) byId.set(n.id, n);
  const failures: FailuresFile = { failures: [...byId.values()].sort((a, b) => a.id.localeCompare(b.id)), unknowns: [...new Set([...previous.unknowns, ...delta.unknowns])] };
  await writeStaging(ctx, 'failures.json', failures);
  return { failures, delta };
}

export async function loadJournal(ctx: PhaseContext): Promise<Journal> {
  return (await readStaging<Journal>(ctx, 'journal.json')) ?? { troubleshooting: [], changelog: [] };
}

/** 저널에 이번 Run의 항목을 append한다(같은 id/제목은 갱신). */
export async function appendJournal(ctx: PhaseContext, delta: Pick<FailureDelta, 'troubleshooting' | 'changelog'>): Promise<Journal> {
  const journal = await loadJournal(ctx);
  const date = today();
  for (const t of delta.troubleshooting) {
    const i = journal.troubleshooting.findIndex((x) => x.id === t.id);
    const entry = { ...t, addedAt: i >= 0 ? journal.troubleshooting[i].addedAt : date };
    if (i >= 0) journal.troubleshooting[i] = entry; else journal.troubleshooting.push(entry);
  }
  for (const c of delta.changelog) {
    if (c.significance === 'TRIVIAL') continue;
    if (!journal.changelog.some((x) => x.date === date && x.title === c.title)) journal.changelog.push({ ...c, date, run: ctx.run.name });
  }
  await writeStaging(ctx, 'journal.json', journal);
  return journal;
}

/** INITIAL: 실패 기록에서 troubleshootingWorthy 항목을 저널로 옮기고 "최초 문서화" 항목을 남긴다. */
export async function seedJournalFromFailures(ctx: PhaseContext, failures: FailuresFile, featureCount: number): Promise<Journal> {
  return appendJournal(ctx, {
    troubleshooting: failures.failures.filter((f) => f.troubleshootingWorthy).map((f) => ({
      id: `TS${f.id.replace('FAIL', '')}`, symptom: f.symptom, cause: f.cause, howToConfirm: f.recurrenceProcedure, resolution: [f.finalSolution || f.currentWorkaround].filter(Boolean), relatedCode: f.relatedFiles, status: f.causeConfidence === 'CONFIRMED' ? 'CONFIRMED' : f.causeConfidence === 'INFERRED' ? 'INFERRED' : 'UNKNOWN',
    })),
    changelog: [{ title: '최초 문서화(INITIAL)', classification: ['DOCUMENTATION_ONLY'], description: `프로젝트 전체를 분석해 기능 ${featureCount}개와 실패 기록 ${failures.failures.length}건을 문서화했다.`, impact: [], relatedDocs: ['README.md'], commits: [], significance: 'MAJOR' }],
  });
}
