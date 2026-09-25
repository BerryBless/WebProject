import { readFileSync } from 'node:fs';
import path from 'node:path';
import type { HarnessConfig } from './config.js';
import { isSecretPath, listSourceFiles } from './fsx.js';

/** 디렉터리별로 묶은 파일 목록 텍스트. 내용은 넣지 않는다(에이전트가 Read로 읽는다). */
export function fileTree(root: string, cfg: HarnessConfig, files: string[] = listSourceFiles(root, cfg)): string {
  const groups = new Map<string, string[]>();
  for (const f of files) {
    const dir = path.posix.dirname(f);
    if (!groups.has(dir)) groups.set(dir, []);
    groups.get(dir)!.push(path.posix.basename(f));
  }
  const lines: string[] = [];
  for (const [dir, names] of [...groups.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    lines.push(`${dir === '.' ? '(root)' : dir}/`);
    for (const n of names) lines.push(`  ${n}`);
  }
  return lines.join('\n');
}

const MANIFEST_NAMES = ['package.json', 'Directory.Packages.props', 'docker-compose.yml', 'compose.yml', 'compose.yaml', 'Dockerfile', 'Caddyfile', '.env.example', 'global.json'];

/** 빌드·배포·CI 매니페스트 경로 목록(값이 아니라 경로만). */
export function manifestFiles(files: string[]): string[] {
  return files.filter((f) => {
    const base = path.posix.basename(f);
    return base.endsWith('.csproj') || base.endsWith('.slnx') || base.endsWith('.sln') || MANIFEST_NAMES.includes(base) || f.startsWith('.github/workflows/') || f.startsWith('deploy/');
  });
}

export interface TruthLists {
  endpoints: { method: string; path: string; file: string }[];
  entities: { name: string; file: string }[];
  configKeys: { key: string; file: string }[];
  pages: { route: string; file: string }[];
  spaRoutes: { route: string; file: string }[];
}

const ENDPOINT_RE = /\.Map(Get|Post|Put|Delete|Patch)\(\s*"([^"]*)"/g;
const DBSET_RE = /DbSet<(\w+)>/g;
const PAGE_DIRECTIVE_RE = /^@page\s+"([^"]*)"/m;
const SPA_ROUTE_RE = /(?:path\s*[:=]\s*|<Route\s+path=)["'`]([^"'`]+)["'`]/g;

/**
 * 코드에서 정규식으로 뽑은 "정답 목록". 검증 단계의 Missing/Hallucination 대조와 변경 감지 심볼 힌트에 쓴다.
 * 정밀도가 완벽할 필요는 없다. 값(비밀)은 절대 읽지 않는다: 설정 파일은 키 이름만 추출한다.
 */
export function truthLists(root: string, cfg: HarnessConfig, files: string[] = listSourceFiles(root, cfg)): TruthLists {
  const out: TruthLists = { endpoints: [], entities: [], configKeys: [], pages: [], spaRoutes: [] };
  for (const rel of files) {
    const abs = path.join(root, rel);
    if (rel.endsWith('.cs')) {
      const text = readFileSync(abs, 'utf8');
      for (const m of text.matchAll(ENDPOINT_RE)) out.endpoints.push({ method: m[1].toUpperCase(), path: m[2], file: rel });
      for (const m of text.matchAll(DBSET_RE)) if (!out.entities.some((e) => e.name === m[1])) out.entities.push({ name: m[1], file: rel });
    } else if (rel.endsWith('.cshtml')) {
      const base = path.posix.basename(rel);
      if (base.startsWith('_') || rel.includes('/Shared/')) continue;
      const text = readFileSync(abs, 'utf8');
      const m = PAGE_DIRECTIVE_RE.exec(text);
      out.pages.push({ route: m?.[1] ?? '(convention) ' + base.replace('.cshtml', ''), file: rel });
    } else if (rel.endsWith('.tsx') || rel.endsWith('.ts')) {
      if (/\.(test|spec)\.tsx?$/.test(rel)) continue;
      const text = readFileSync(abs, 'utf8');
      for (const m of text.matchAll(SPA_ROUTE_RE)) if (m[1].startsWith('/')) out.spaRoutes.push({ route: m[1], file: rel });
    } else if (/appsettings.*\.json$/.test(path.posix.basename(rel))) {
      try {
        const obj = JSON.parse(readFileSync(abs, 'utf8')) as Record<string, unknown>;
        for (const [k, v] of Object.entries(obj)) {
          out.configKeys.push({ key: k, file: rel });
          if (v && typeof v === 'object' && !Array.isArray(v)) for (const k2 of Object.keys(v)) out.configKeys.push({ key: `${k}:${k2}`, file: rel });
        }
      } catch { /* 값이 아닌 키만 필요하므로 파싱 실패는 무시 */ }
    }
  }
  return out;
}

export function formatTruthLists(t: TruthLists): string {
  const lines: string[] = [];
  lines.push('### 코드에서 추출한 엔드포인트');
  for (const e of t.endpoints) lines.push(`- ${e.method} ${e.path}  (${e.file})`);
  lines.push('', '### DbSet 엔티티');
  for (const e of t.entities) lines.push(`- ${e.name}  (${e.file})`);
  lines.push('', '### Razor 페이지');
  for (const p of t.pages) lines.push(`- ${p.route}  (${p.file})`);
  lines.push('', '### SPA 라우트');
  for (const r of t.spaRoutes) lines.push(`- ${r.route}  (${r.file})`);
  lines.push('', '### 설정 키(값 제외)');
  for (const c of t.configKeys) lines.push(`- ${c.key}  (${c.file})`);
  return lines.join('\n');
}

/** 객체에서 지정한 키만 뽑아 JSON 텍스트로. 이전 단계 산출물을 "필요한 절만" 주입할 때 쓴다. */
export function excerptJson(obj: Record<string, unknown>, keys: string[], maxChars = 60_000): string {
  const picked: Record<string, unknown> = {};
  for (const k of keys) if (k in obj) picked[k] = obj[k];
  let text = JSON.stringify(picked, null, 1);
  if (text.length > maxChars) text = text.slice(0, maxChars) + '\n…(잘림)';
  return text;
}

/** 비밀 파일을 걸러낸 경로 목록 텍스트. */
export function pathList(files: string[], cfg: HarnessConfig): string {
  return files.filter((f) => !isSecretPath(f, cfg)).map((f) => `- ${f}`).join('\n');
}
