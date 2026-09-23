import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import type { HarnessConfig } from './config.js';

export function sha256(data: string | Buffer): string {
  return createHash('sha256').update(data).digest('hex');
}

export function toPosix(p: string): string {
  return p.split(path.sep).join('/');
}

let tmpCounter = 0;

/** 임시 파일에 쓴 뒤 rename한다. 중간에 죽어도 반쯤 쓰인 파일이 정본 자리에 남지 않는다. 같은 파일에 동시에 써도 tmp 이름이 겹치지 않는다. */
export async function atomicWriteText(file: string, text: string): Promise<void> {
  await mkdir(path.dirname(file), { recursive: true });
  const tmp = `${file}.${process.pid}.${++tmpCounter}.tmp`;
  await writeFile(tmp, text, 'utf8');
  // Windows에서는 다른 프로세스(백신·인덱서)나 동시 rename이 대상 파일을 잠깐 잡고 있으면 EPERM/EBUSY가 난다. 짧게 재시도한다.
  for (let attempt = 0; ; attempt++) {
    try {
      await rename(tmp, file);
      return;
    } catch (e) {
      const code = (e as NodeJS.ErrnoException).code;
      if (attempt >= 5 || !(code === 'EPERM' || code === 'EBUSY' || code === 'EACCES')) {
        await rm(tmp, { force: true }).catch(() => undefined);
        throw e;
      }
      await new Promise((r) => setTimeout(r, 20 * (attempt + 1)));
    }
  }
}

export async function atomicWriteJson(file: string, data: unknown): Promise<void> {
  await atomicWriteText(file, JSON.stringify(data, null, 2) + '\n');
}

export async function readJson<T>(file: string): Promise<T> {
  return JSON.parse(await readFile(file, 'utf8')) as T;
}

export function readJsonSync<T>(file: string): T {
  return JSON.parse(readFileSync(file, 'utf8')) as T;
}

export async function readJsonOrNull<T>(file: string): Promise<T | null> {
  if (!existsSync(file)) return null;
  return readJson<T>(file);
}

export function hashFile(file: string): string {
  return sha256(readFileSync(file));
}

function isExcluded(rel: string, exclude: string[]): boolean {
  const segments = rel.split('/');
  for (const ex of exclude) {
    const e = ex.endsWith('/') ? ex.slice(0, -1) : ex;
    if (e.includes('/')) {
      if (rel === e || rel.startsWith(e + '/')) return true;
    } else if (segments.slice(0, -1).includes(e)) {
      return true;
    }
  }
  return false;
}

export function isSourceFile(rel: string, cfg: HarnessConfig): boolean {
  const base = path.posix.basename(rel);
  return cfg.project.source_extensions.some((ext) => (ext.startsWith('.') ? base.endsWith(ext) : base === ext));
}

/** 분석 대상 파일 목록(저장소 루트 기준 `/` 경로, 정렬). 제외 규칙과 확장자 규칙을 적용한다. */
export function listSourceFiles(root: string, cfg: HarnessConfig): string[] {
  const out: string[] = [];
  const walk = (dir: string) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const abs = path.join(dir, entry.name);
      const rel = toPosix(path.relative(root, abs));
      if (isExcluded(rel + (entry.isDirectory() ? '/' : ''), cfg.project.exclude)) continue;
      if (entry.isDirectory()) walk(abs);
      else if (entry.isFile() && isSourceFile(rel, cfg)) out.push(rel);
    }
  };
  walk(root);
  return out.sort();
}

export function isSecretPath(rel: string, cfg: HarnessConfig): boolean {
  const base = path.posix.basename(rel);
  return cfg.project.secret_globs.some((g) => {
    const pattern = g.replace(/^\*\*\//, '');
    const re = new RegExp('^' + pattern.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*') + '$');
    return re.test(base);
  });
}

export function fileSize(file: string): number {
  return statSync(file).size;
}
