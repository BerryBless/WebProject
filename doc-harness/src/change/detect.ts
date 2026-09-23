import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fingerprint } from '../baseline.js';
import type { HarnessConfig, HarnessPaths } from '../config.js';
import { hashFile, listSourceFiles } from '../fsx.js';
import { commitExists, diffHunks, headCommit, isGitRepo, nameStatusSince, untrackedFiles } from '../git.js';
import type { Baseline, ChangeSet, ChangedFile } from '../types.js';

const SYMBOL_PATTERNS: RegExp[] = [
  /\b(?:class|record|interface|enum|struct)\s+([A-Z]\w+)/g,
  /\.Map(?:Get|Post|Put|Delete|Patch)\(\s*"([^"]+)"/g,
  /\bexport\s+(?:async\s+)?(?:function|const|class|interface|type)\s+(\w+)/g,
  /\b(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\],? ]+\s+([A-Z]\w+)\s*\(/g,
  /\bfunction\s+(\w+)\s*\(/g,
];

export function extractSymbols(lines: string[]): string[] {
  const found = new Set<string>();
  for (const line of lines) {
    for (const re of SYMBOL_PATTERNS) {
      re.lastIndex = 0;
      for (const m of line.matchAll(re)) found.add(m[1]);
    }
  }
  return [...found].sort();
}

const TEST_PATH_RE = /(\.Tests\/|\/test\/|\/tests\/|\/e2e\/|\.test\.|\.spec\.|__tests__\/)/;

export function requiresAnalysis(file: string, cfg: HarnessConfig): boolean {
  if (file.endsWith('.md')) return false;
  if (cfg.project.tooling_dirs.some((d) => file.startsWith(d))) return false;
  if (TEST_PATH_RE.test(file)) return false;
  return true;
}

export interface FileHash { path: string; hash: string }

export function hashSourceFiles(root: string, cfg: HarnessConfig): FileHash[] {
  return listSourceFiles(root, cfg).map((p) => ({ path: p, hash: hashFile(path.join(root, p)) }));
}

/**
 * baseline과 현재 working tree를 비교한다(스펙 §6.4). 해시 대조가 1차, git은 rename·hunk 보강.
 * baseline이 없으면 모든 파일이 ADDED다(INITIAL). `withHunks=false`면 줄 단위 diff를 생략한다.
 */
export async function detectChanges(paths: HarnessPaths, cfg: HarnessConfig, baseline: Baseline | null, opts: { withHunks?: boolean } = {}): Promise<ChangeSet> {
  const root = paths.projectRoot;
  const withHunks = opts.withHunks ?? baseline !== null;
  const current = hashSourceFiles(root, cfg);
  const fp = fingerprint(current);
  const inRepo = await isGitRepo(root);
  const head = inRepo ? await headCommit(root) : '';
  const untracked = inRepo ? await untrackedFiles(root) : new Set<string>();
  const baselineCommit = baseline && inRepo && (await commitExists(root, baseline.baselineCommit)) ? baseline.baselineCommit : null;

  const files: ChangedFile[] = [];
  const baseFiles = baseline?.files ?? {};
  const currentByPath = new Map(current.map((f) => [f.path, f.hash]));
  const consumedOld = new Set<string>();

  // rename 후보: baseline에서 사라진 파일 중 해시가 같은 것
  const baseByHash = new Map<string, string[]>();
  for (const [p, info] of Object.entries(baseFiles)) {
    if (!currentByPath.has(p)) baseByHash.set(info.hash, [...(baseByHash.get(info.hash) ?? []), p]);
  }
  const gitRenames = new Map<string, string>();
  if (baselineCommit) {
    for (const ns of await nameStatusSince(root, baselineCommit)) if (ns.status === 'R' && ns.oldPath) gitRenames.set(ns.path, ns.oldPath);
  }

  for (const f of current) {
    const base = baseFiles[f.path];
    const isUntracked = untracked.has(f.path);
    if (!base) {
      if (baseline === null) {
        files.push(makeChanged(f.path, 'ADDED', isUntracked, undefined, withHunks ? readAll(root, f.path) : [], [], cfg));
        continue;
      }
      const oldByGit = gitRenames.get(f.path);
      const oldByHash = baseByHash.get(f.hash)?.find((p) => !consumedOld.has(p));
      const oldPath = oldByGit && baseFiles[oldByGit] && !consumedOld.has(oldByGit) ? oldByGit : oldByHash;
      if (oldPath) {
        consumedOld.add(oldPath);
        const hunks = withHunks && baseFiles[oldPath].hash !== f.hash ? await hunksFor(root, f.path, baselineCommit, isUntracked) : { added: [], removed: [] };
        files.push(makeChanged(f.path, 'RENAMED', isUntracked, oldPath, hunks.added, hunks.removed, cfg));
      } else {
        const hunks = withHunks ? { added: readAll(root, f.path), removed: [] } : { added: [], removed: [] };
        files.push(makeChanged(f.path, isUntracked ? 'UNTRACKED' : 'ADDED', isUntracked, undefined, hunks.added, hunks.removed, cfg));
      }
    } else if (base.hash !== f.hash) {
      const hunks = withHunks ? await hunksFor(root, f.path, baselineCommit, isUntracked) : { added: [], removed: [] };
      files.push(makeChanged(f.path, 'MODIFIED', isUntracked, undefined, hunks.added, hunks.removed, cfg));
    }
  }
  for (const p of Object.keys(baseFiles)) {
    if (!currentByPath.has(p) && !consumedOld.has(p)) files.push(makeChanged(p, 'DELETED', false, undefined, [], [], cfg));
  }
  files.sort((a, b) => a.file.localeCompare(b.file));
  return { fingerprint: fp, baselineCommit, headCommit: head, files };
}

function readAll(root: string, rel: string): string[] {
  try {
    const text = readFileSync(path.join(root, rel), 'utf8');
    return text.length > 200_000 ? text.slice(0, 200_000).split('\n') : text.split('\n');
  } catch {
    return [];
  }
}

async function hunksFor(root: string, rel: string, baselineCommit: string | null, isUntracked: boolean): Promise<{ added: string[]; removed: string[] }> {
  if (isUntracked) return { added: readAll(root, rel), removed: [] };
  return diffHunks(root, rel, baselineCommit);
}

function makeChanged(file: string, changeType: ChangedFile['changeType'], untracked: boolean, oldPath: string | undefined, added: string[], removed: string[], cfg: HarnessConfig): ChangedFile {
  const cap = (xs: string[]) => (xs.length > 400 ? xs.slice(0, 400) : xs);
  return {
    file,
    changeType,
    oldPath,
    untracked,
    addedLines: cap(added),
    removedLines: cap(removed),
    relatedSymbols: extractSymbols([...added, ...removed]),
    possibleFeatures: [],
    requiresAnalysis: changeType === 'DELETED' ? requiresAnalysis(file, cfg) : requiresAnalysis(file, cfg),
  };
}
