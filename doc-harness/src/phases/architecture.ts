import { readFileSync } from 'node:fs';
import path from 'node:path';
import { excerptJson, pathList } from '../context.js';
import { listSourceFiles } from '../fsx.js';
import { buildPrompt } from '../prompts.js';
import type { Architecture, ChangeSet, Inventory } from '../types.js';
import { callClaude, type PhaseContext } from './common.js';

const ENTRY_HINT_RE = /(WebApplication\.CreateBuilder|createRoot\(|ReactDOM|app\.Map\w+Endpoints|AddHostedService|BackgroundService|UseMiddleware|app\.Use)/;

/** 진입점·DI·미들웨어가 있을 법한 파일. 내용을 정규식으로 훑되 프롬프트에는 경로만 넣는다. */
export function entryFiles(root: string, files: string[]): string[] {
  const out: string[] = [];
  for (const f of files) {
    const base = path.posix.basename(f);
    if (base === 'Program.cs' || base === 'main.tsx' || base === 'App.tsx' || base === 'Caddyfile' || base.startsWith('compose') || base === 'docker-compose.yml' || base === 'Dockerfile') { out.push(f); continue; }
    if ((f.endsWith('.cs') || f.endsWith('.tsx')) && f.length < 200) {
      try {
        if (ENTRY_HINT_RE.test(readFileSync(path.join(root, f), 'utf8'))) out.push(f);
      } catch { /* skip */ }
    }
  }
  return [...new Set(out)];
}

export function changeHintsText(changes: ChangeSet | null): string {
  if (!changes || !changes.files.length) return '(전체 분석 — 변경 힌트 없음)';
  return changes.files.slice(0, 80).map((f) => `- ${f.changeType} ${f.file}${f.oldPath ? ` (from ${f.oldPath})` : ''}${f.relatedSymbols.length ? ` — 심볼: ${f.relatedSymbols.slice(0, 8).join(', ')}` : ''}`).join('\n');
}

export async function runArchitecture(ctx: PhaseContext, inventory: Inventory, previous: Architecture | null, changes: ChangeSet | null): Promise<Architecture> {
  const files = listSourceFiles(ctx.paths.projectRoot, ctx.cfg);
  const prompt = buildPrompt('02_architecture', {
    inventory: excerptJson(inventory as unknown as Record<string, unknown>, ['project', 'frameworks', 'runtimes', 'entryPoints', 'directories', 'databases', 'caches', 'queues', 'externalSystems', 'containers'], 40_000),
    entryFiles: pathList(entryFiles(ctx.paths.projectRoot, files), ctx.cfg) || '(없음)',
    previous: previous ? excerptJson(previous as unknown as Record<string, unknown>, ['components', 'relations', 'controlFlows', 'decisions'], 40_000) : '(없음 — 최초 분석)',
    changeHints: changeHintsText(changes),
  });
  return callClaude<Architecture>(ctx, { itemId: 'architecture', phase: 'architecture', schemaName: 'architecture', prompt, outFile: 'architecture.json' });
}
