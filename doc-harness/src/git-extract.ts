import { readFileSync } from 'node:fs';
import path from 'node:path';
import type { HarnessConfig } from './config.js';
import { listSourceFiles } from './fsx.js';
import { git, isGitRepo } from './git.js';

export interface GitCommit {
  sha: string;
  date: string;
  subject: string;
  body: string;
  keywordHits: string[];
  files: string[];
  diffExcerpt: string;
}

export interface GitExtract {
  since: string | null;
  commits: GitCommit[];
  markers: { file: string; line: number; text: string }[];
  workingTreeDiffExcerpt: string;
}

const SEP = '\u001f';
const REC = '\u001e';

/**
 * 실패 이력 분석용 Git 추출. 키워드 히트 커밋만 변경 파일·diff(상한)를 가져온다.
 * since가 있으면 `since..HEAD`, 없으면 전체 이력.
 */
export async function extractGitHistory(root: string, cfg: HarnessConfig, since: string | null, includeWorkingTree: boolean, files: string[] = listSourceFiles(root, cfg)): Promise<GitExtract> {
  const markers = extractMarkers(root, cfg, files);
  if (!(await isGitRepo(root)) || !cfg.git.analyze_history) return { since, commits: [], markers, workingTreeDiffExcerpt: '' };

  const range = since ? [`${since}..HEAD`] : [];
  let log = '';
  try {
    log = await git(root, ['log', `--format=%H${SEP}%aI${SEP}%s${SEP}%b${REC}`, ...range]);
  } catch {
    return { since, commits: [], markers, workingTreeDiffExcerpt: '' };
  }
  const keywords = cfg.git.failure_keywords.map((k) => k.toLowerCase());
  const commits: GitCommit[] = [];
  for (const chunk of log.split(REC)) {
    const rec = chunk.trim();
    if (!rec) continue;
    const [sha, date, subject, body = ''] = rec.split(SEP);
    const hay = `${subject}\n${body}`.toLowerCase();
    const hits = keywords.filter((k) => hay.includes(k));
    const commit: GitCommit = { sha, date, subject, body: body.trim(), keywordHits: hits, files: [], diffExcerpt: '' };
    if (hits.length) {
      try {
        commit.files = (await git(root, ['show', '--name-only', '--format=', sha])).split('\n').map((l) => l.trim()).filter(Boolean);
        const diff = await git(root, ['show', '--format=', '--stat=120', '-p', '--no-color', sha, '--', ...diffPathFilter(commit.files, cfg)]);
        commit.diffExcerpt = cap(diff, cfg.git.max_diff_bytes_per_commit);
      } catch { /* 병합 커밋 등 show가 실패하면 메시지만 남긴다 */ }
    }
    commits.push(commit);
  }

  let workingTreeDiffExcerpt = '';
  if (includeWorkingTree) {
    try {
      workingTreeDiffExcerpt = cap(await git(root, ['diff', 'HEAD', '--no-color', '--stat=120', '-p']), cfg.git.max_diff_bytes_per_commit);
    } catch { /* HEAD 없음 */ }
  }
  return { since, commits, markers, workingTreeDiffExcerpt };
}

function diffPathFilter(files: string[], cfg: HarnessConfig): string[] {
  const src = files.filter((f) => !f.endsWith('.md') && !cfg.project.exclude.some((e) => f.startsWith(e)) && !f.startsWith('_workspace/'));
  return src.length ? src : files;
}

function cap(text: string, maxBytes: number): string {
  if (Buffer.byteLength(text) <= maxBytes) return text;
  return Buffer.from(text).subarray(0, maxBytes).toString('utf8') + '\n…(diff 잘림)';
}

export function extractMarkers(root: string, cfg: HarnessConfig, files: string[]): { file: string; line: number; text: string }[] {
  const re = new RegExp(`\\b(${cfg.git.marker_pattern})\\b`, 'i');
  const out: { file: string; line: number; text: string }[] = [];
  for (const rel of files) {
    if (rel.endsWith('.md') || cfg.project.tooling_dirs.some((d) => rel.startsWith(d))) continue;
    let text: string;
    try { text = readFileSync(path.join(root, rel), 'utf8'); } catch { continue; }
    const lines = text.split('\n');
    for (let i = 0; i < lines.length; i++) {
      if (re.test(lines[i])) out.push({ file: rel, line: i + 1, text: lines[i].trim().slice(0, 200) });
    }
  }
  return out;
}

export function formatGitExtract(x: GitExtract, maxCommits = 60): string {
  const hits = x.commits.filter((c) => c.keywordHits.length);
  const others = x.commits.filter((c) => !c.keywordHits.length);
  const lines: string[] = [];
  lines.push(`범위: ${x.since ? `${x.since.slice(0, 12)}..HEAD` : '전체'} — 커밋 ${x.commits.length}개(키워드 히트 ${hits.length}개)`, '');
  for (const c of hits.slice(0, maxCommits)) {
    lines.push(`### ${c.sha.slice(0, 10)} ${c.date.slice(0, 10)} — ${c.subject}`, `키워드: ${c.keywordHits.join(', ')}`);
    if (c.body) lines.push('', c.body);
    if (c.files.length) lines.push('', '변경 파일:', ...c.files.slice(0, 40).map((f) => `- ${f}`));
    if (c.diffExcerpt) lines.push('', '```diff', c.diffExcerpt, '```');
    lines.push('');
  }
  if (others.length) {
    lines.push('### 그 밖의 커밋(제목만)');
    for (const c of others.slice(0, 120)) lines.push(`- ${c.sha.slice(0, 10)} ${c.date.slice(0, 10)} ${c.subject}`);
  }
  if (x.workingTreeDiffExcerpt) lines.push('', '### 커밋되지 않은 working tree diff', '```diff', x.workingTreeDiffExcerpt, '```');
  return lines.join('\n');
}

export function formatMarkers(markers: GitExtract['markers'], onlyFiles?: Set<string>): string {
  const list = onlyFiles ? markers.filter((m) => onlyFiles.has(m.file)) : markers;
  if (!list.length) return '(마커 없음)';
  return list.slice(0, 200).map((m) => `- ${m.file}:${m.line}  ${m.text}`).join('\n');
}
