import { buildPrompt } from '../prompts.js';
import { callClaude, sessionContextBlock, type PhaseContext } from '../phases/common.js';
import type { ChangeSet, ChangedFile, Classification, FeatureSummary } from '../types.js';

export function formatChanges(changes: ChangeSet, opts: { maxFiles?: number; maxLines?: number } = {}): string {
  const maxFiles = opts.maxFiles ?? 60;
  const maxLines = opts.maxLines ?? 50;
  const blocks: string[] = [];
  const files = [...changes.files].sort((a, b) => Number(b.requiresAnalysis) - Number(a.requiresAnalysis));
  for (const f of files.slice(0, maxFiles)) blocks.push(formatChangedFile(f, maxLines));
  if (files.length > maxFiles) blocks.push(`(그 밖에 ${files.length - maxFiles}개 파일: ${files.slice(maxFiles).map((f) => f.file).join(', ')})`);
  return blocks.join('\n\n') || '(변경 없음)';
}

export function formatChangedFile(f: ChangedFile, maxLines = 50): string {
  const head = `### ${f.changeType} ${f.file}${f.oldPath ? ` (from ${f.oldPath})` : ''}${f.untracked ? ' [untracked]' : ''}${f.requiresAnalysis ? '' : ' [문서·테스트·도구 전용]'}`;
  if (!f.requiresAnalysis || (!f.addedLines.length && !f.removedLines.length)) return `${head}\n심볼: ${f.relatedSymbols.join(', ') || '-'}`;
  const removed = f.removedLines.slice(0, maxLines).map((l) => `- ${l}`);
  const added = f.addedLines.slice(0, maxLines).map((l) => `+ ${l}`);
  const more = f.addedLines.length + f.removedLines.length > maxLines * 2 ? '\n(…hunk 잘림)' : '';
  return `${head}\n심볼: ${f.relatedSymbols.join(', ') || '-'}\n\`\`\`diff\n${[...removed, ...added].join('\n')}${more}\n\`\`\``;
}

export function featureIndexJson(features: FeatureSummary[]): string {
  return JSON.stringify(features.map((f) => ({ id: f.id, name: f.name, slug: f.slug, entryPoints: f.entryPoints, relatedFiles: f.relatedFiles, status: f.status })), null, 1);
}

export async function classifyChanges(ctx: PhaseContext, changes: ChangeSet, features: FeatureSummary[]): Promise<Classification> {
  const prompt = buildPrompt('I02_classify', {
    features: featureIndexJson(features),
    changes: formatChanges(changes),
    sessionContext: sessionContextBlock(ctx),
  });
  const out = await callClaude<Classification>(ctx, { itemId: 'classify', phase: 'classify', schemaName: 'classification', prompt, outFile: 'classification.json', extraHash: changes.fingerprint });
  // 누락된 파일은 UNKNOWN으로 채워 하류 규칙이 항상 전체 파일을 본다.
  const seen = new Set(out.items.map((i) => i.file));
  for (const f of changes.files) {
    if (!seen.has(f.file)) out.items.push({ file: f.file, classifications: [f.requiresAnalysis ? 'UNKNOWN' : 'DOCUMENTATION_ONLY'], significance: f.requiresAnalysis ? 'MINOR' : 'TRIVIAL', possibleFeatures: [], rationale: '분류 응답에 없어 하네스가 보충' });
  }
  return out;
}
