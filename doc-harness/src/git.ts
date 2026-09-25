import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

export async function git(root: string, args: string[], opts: { maxBuffer?: number } = {}): Promise<string> {
  const { stdout } = await execFileAsync('git', args, { cwd: root, maxBuffer: opts.maxBuffer ?? 64 * 1024 * 1024, windowsHide: true, encoding: 'utf8' });
  return stdout;
}

export async function headCommit(root: string): Promise<string> {
  return (await git(root, ['rev-parse', 'HEAD'])).trim();
}

export async function commitExists(root: string, sha: string): Promise<boolean> {
  if (!sha) return false;
  try {
    await git(root, ['cat-file', '-e', `${sha}^{commit}`]);
    return true;
  } catch {
    return false;
  }
}

export async function untrackedFiles(root: string): Promise<Set<string>> {
  const out = await git(root, ['ls-files', '--others', '--exclude-standard']);
  return new Set(out.split('\n').map((l) => l.trim()).filter(Boolean));
}

export interface NameStatus {
  status: 'A' | 'M' | 'D' | 'R';
  path: string;
  oldPath?: string;
}

/** baseline 커밋 이후(working tree 포함) 이름 상태. rename은 -M으로 감지한다. */
export async function nameStatusSince(root: string, sha: string): Promise<NameStatus[]> {
  const out = await git(root, ['diff', '--name-status', '-M', sha]);
  const result: NameStatus[] = [];
  for (const line of out.split('\n')) {
    if (!line.trim()) continue;
    const parts = line.split('\t');
    const code = parts[0][0] as NameStatus['status'];
    if (code === 'R') result.push({ status: 'R', oldPath: parts[1], path: parts[2] });
    else if (code === 'A' || code === 'M' || code === 'D') result.push({ status: code, path: parts[1] });
  }
  return result;
}

/** 파일 하나의 추가/삭제 줄. sinceSha가 없으면 HEAD 대비 working tree. */
export async function diffHunks(root: string, file: string, sinceSha: string | null): Promise<{ added: string[]; removed: string[] }> {
  let out = '';
  try {
    out = await git(root, ['diff', '--no-color', '--unified=0', sinceSha ?? 'HEAD', '--', file]);
  } catch {
    return { added: [], removed: [] };
  }
  const added: string[] = [];
  const removed: string[] = [];
  for (const line of out.split('\n')) {
    if (line.startsWith('+++') || line.startsWith('---')) continue;
    if (line.startsWith('+')) added.push(line.slice(1));
    else if (line.startsWith('-')) removed.push(line.slice(1));
  }
  return { added, removed };
}

export async function isGitRepo(root: string): Promise<boolean> {
  try {
    await git(root, ['rev-parse', '--is-inside-work-tree']);
    return true;
  } catch {
    return false;
  }
}
