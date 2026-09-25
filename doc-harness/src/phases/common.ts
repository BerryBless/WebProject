import { existsSync } from 'node:fs';
import { cp, mkdir, readdir } from 'node:fs/promises';
import path from 'node:path';
import type { ClaudeRunner } from '../claude.js';
import type { HarnessConfig, HarnessPaths } from '../config.js';
import { atomicWriteJson, readJson, readJsonOrNull, sha256 } from '../fsx.js';
import type { Run } from '../run.js';
import type { SchemaName } from '../schema.js';
import type { CurrentWorkspace, FeatureAnalysis } from '../types.js';

export type Scope =
  | { kind: 'ALL' }
  | { kind: 'PARTIAL'; features: string[]; changedFiles: string[]; docs: string[]; diagrams: string[] };

export interface PhaseContext {
  cfg: HarnessConfig;
  paths: HarnessPaths;
  run: Run;
  runner: ClaudeRunner;
  scope: Scope;
  log: (msg: string) => void;
  /** 세션 맥락(inbox)의 본문. 없으면 빈 문자열. */
  sessionContext: string;
}

export interface CallOptions {
  itemId: string;
  phase: string;
  schemaName: SchemaName;
  prompt: string;
  /** staging/current 기준 상대 경로. 결과 JSON을 여기에 쓴다. */
  outFile: string;
  /** 프롬프트 외에 inputHash에 섞을 값(예: 관련 파일 해시). */
  extraHash?: string;
}

const ISO_DATE_RE = /\b20\d{2}-\d{2}-\d{2}\b/g;

/**
 * 입력 해시. 프롬프트의 오늘 날짜는 실행일마다 바뀌므로 ISO 날짜(YYYY-MM-DD)를 전부 자리표시자로 치환해 계산한다.
 * 특정 날짜만 치환하면 다른 날짜 문자열(파일명·커밋 날짜)이 섞인 프롬프트에서 실행일에 따라 해시가 달라진다(2026-09-24 실측).
 */
export function stableInputHash(prompt: string, extraHash?: string): string {
  return sha256(prompt.replace(ISO_DATE_RE, '{{date}}') + '\n' + (extraHash ?? ''));
}

/**
 * 항목 하나를 Claude에 물어 결과를 staging/current/<outFile>에 쓴다.
 * 같은 입력으로 이미 SUCCESS면 저장된 결과를 다시 읽는다(resume).
 */
export async function callClaude<T>(ctx: PhaseContext, opts: CallOptions): Promise<T> {
  const outAbs = path.join(ctx.run.staging.current, opts.outFile);
  const inputHash = stableInputHash(opts.prompt, opts.extraHash);
  return ctx.run.runItem<T>(
    opts.itemId,
    inputHash,
    async (report) => {
      ctx.log(`▶ ${opts.itemId} (${opts.phase})`);
      const r = await ctx.runner.run({ id: opts.itemId, phase: opts.phase, schemaName: opts.schemaName, prompt: opts.prompt, cwd: ctx.paths.projectRoot, logDir: ctx.run.logs });
      report(r.costUsd, r.attempts);
      if (!r.ok) throw new Error(`${opts.itemId}: ${r.error}`);
      await atomicWriteJson(outAbs, r.output);
      ctx.log(`✓ ${opts.itemId} ($${r.costUsd.toFixed(3)}, ${r.attempts}회)`);
      return r.output as T;
    },
    existsSync(outAbs) ? () => readJson<T>(outAbs) : undefined,
  );
}

export function stagingFile(ctx: PhaseContext, rel: string): string {
  return path.join(ctx.run.staging.current, rel);
}

export async function readStaging<T>(ctx: PhaseContext, rel: string): Promise<T | null> {
  return readJsonOrNull<T>(stagingFile(ctx, rel));
}

export async function writeStaging(ctx: PhaseContext, rel: string, data: unknown): Promise<void> {
  await atomicWriteJson(stagingFile(ctx, rel), data);
}

/** INCREMENTAL 시작 시 정본 current/를 staging/current로 복사해 바뀌지 않는 산출물을 이어받는다. */
export async function seedStagingFromCurrent(ctx: PhaseContext): Promise<void> {
  if (!existsSync(ctx.paths.current)) return;
  await mkdir(ctx.run.staging.current, { recursive: true });
  await cp(ctx.paths.current, ctx.run.staging.current, { recursive: true, force: false, errorOnExist: false });
}

/** staging/current(또는 지정 디렉터리)에서 워크스페이스 전체를 읽는다. 없는 파일은 예외. */
export async function loadWorkspace(dir: string): Promise<CurrentWorkspace> {
  const need = async <T>(name: string): Promise<T> => {
    const v = await readJsonOrNull<T>(path.join(dir, name));
    if (v === null) throw new Error(`워크스페이스 산출물이 없다: ${name}`);
    return v;
  };
  const featureAnalyses = new Map<string, FeatureAnalysis>();
  const fdir = path.join(dir, 'features');
  if (existsSync(fdir)) {
    for (const f of (await readdir(fdir)).filter((n) => /^F\d{3}\.json$/.test(n)).sort()) {
      featureAnalyses.set(f.replace('.json', ''), await readJson<FeatureAnalysis>(path.join(fdir, f)));
    }
  }
  return {
    inventory: await need('inventory.json'),
    architecture: await need('architecture.json'),
    features: await need('features.json'),
    featureAnalyses,
    data: await need('data.json'),
    api: await need('api.json'),
    failures: await need('failures.json'),
    operations: await need('operations.json'),
  };
}

export function sessionContextBlock(ctx: Pick<PhaseContext, 'sessionContext'>): string {
  if (!ctx.sessionContext.trim()) return '(세션 맥락 없음)';
  return `다음은 직전 개발 세션의 요약이다. **최하위 근거**이며 코드로 확인되지 않은 내용은 사실로 쓰지 않는다.\n\n${ctx.sessionContext.trim()}`;
}
