import { spawn } from 'node:child_process';
import { appendFile, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { phaseClaudeConfig, type HarnessConfig } from './config.js';
import { sha256 } from './fsx.js';
import { loadSchema, validate, type SchemaName } from './schema.js';

export interface ClaudeRequest {
  /** 로그 파일명과 Fake 핸들러 키. 예: `feature:F003`, `inventory` */
  id: string;
  prompt: string;
  schemaName: SchemaName;
  /** harness.yaml `claude.phases`의 키(모델·예산 선택). */
  phase: string;
  cwd: string;
  logDir: string;
}

export type ClaudeResult =
  | { ok: true; output: unknown; costUsd: number; sessionId: string; durationMs: number; attempts: number }
  | { ok: false; error: string; attempts: number; costUsd: number };

export interface ClaudeRunner {
  run(req: ClaudeRequest): Promise<ClaudeResult>;
}

/** 모든 에이전트 호출에 고정되는 인자. 쓰기 도구·Bash가 없고 프로젝트 훅을 읽지 않는다(스펙 §5). */
export const FIXED_ARGS = [
  '-p',
  '--output-format', 'json',
  '--tools', 'Read,Glob,Grep',
  '--setting-sources', 'user',
  '--no-session-persistence',
  '--permission-prompts', 'none',
];

const SECRET_PATTERNS: RegExp[] = [
  /-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----/g,
  new RegExp('AK' + 'IA[0-9A-Z]{16}', 'g'),
  /(["']?(?:Password|Pwd|Secret|Token|ApiKey)["']?\s*[:=]\s*["']?)([^"'\s;,]{4,})/gi,
  /(sk-ant-[A-Za-z0-9_-]{8,})/g,
];

/** 로그에 남기기 전에 비밀값 꼴을 가린다. 값의 형태만 지우고 키는 남긴다. */
export function redactSecrets(text: string): string {
  let out = text;
  out = out.replace(SECRET_PATTERNS[0], '[REDACTED PRIVATE KEY]');
  out = out.replace(SECRET_PATTERNS[1], '[REDACTED]');
  out = out.replace(SECRET_PATTERNS[2], '$1[REDACTED]');
  out = out.replace(SECRET_PATTERNS[3], '[REDACTED]');
  return out;
}

interface CliJson {
  structured_output?: unknown;
  result?: unknown;
  is_error?: boolean;
  total_cost_usd?: number;
  session_id?: string;
  stop_reason?: string;
  num_turns?: number;
}

export interface CliRunnerOptions {
  bin?: string;
  /** 테스트용: 실제 실행 파일 앞에 붙는 인자(예: `['test/fixtures/fake-claude.mjs']` + bin `node`). */
  prefixArgs?: string[];
  backoffMs?: number[];
  env?: NodeJS.ProcessEnv;
}

export class CliClaudeRunner implements ClaudeRunner {
  private readonly bin: string;
  private readonly prefixArgs: string[];
  private readonly backoffMs: number[];
  private readonly env: NodeJS.ProcessEnv;

  constructor(private readonly cfg: HarnessConfig, opts: CliRunnerOptions = {}) {
    this.bin = opts.bin ?? cfg.claude.bin;
    this.prefixArgs = opts.prefixArgs ?? [];
    this.backoffMs = opts.backoffMs ?? [2000, 5000, 10000];
    this.env = opts.env ?? process.env;
  }

  buildArgs(req: ClaudeRequest): string[] {
    const pc = phaseClaudeConfig(this.cfg, req.phase);
    return [
      ...this.prefixArgs,
      ...FIXED_ARGS,
      '--json-schema', JSON.stringify(loadSchema(req.schemaName)),
      '--model', pc.model,
      '--max-budget-usd', String(pc.budget_usd),
      '--effort', pc.effort,
    ];
  }

  async run(req: ClaudeRequest): Promise<ClaudeResult> {
    const pc = phaseClaudeConfig(this.cfg, req.phase);
    const maxAttempts = Math.max(1, this.cfg.retry.max_attempts);
    await mkdir(req.logDir, { recursive: true });
    const safeId = req.id.replace(/[^A-Za-z0-9_.-]/g, '_');
    const logFile = path.join(req.logDir, `${safeId}.log`);
    await writeFile(path.join(req.logDir, `${safeId}.prompt.md`), redactSecrets(req.prompt), 'utf8');

    let prompt = req.prompt;
    let totalCost = 0;
    let lastError = '';
    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
      const started = Date.now();
      const entry: Record<string, unknown> = { id: req.id, phase: req.phase, attempt, model: pc.model, startedAt: new Date(started).toISOString(), promptSha256: sha256(prompt), promptChars: prompt.length };
      const exec = await this.spawnOnce(prompt, req, pc.timeout_sec * 1000);
      entry.durationMs = Date.now() - started;
      entry.exitCode = exec.code;
      if (exec.timedOut) entry.timedOut = true;

      const parsed = parseCliOutput(exec.stdout);
      if (parsed.json) {
        entry.sessionId = parsed.json.session_id;
        entry.stopReason = parsed.json.stop_reason;
        entry.numTurns = parsed.json.num_turns;
        entry.costUsd = parsed.json.total_cost_usd ?? 0;
        totalCost += parsed.json.total_cost_usd ?? 0;
      }

      let failure: string | null = null;
      if (exec.timedOut) failure = `timeout after ${pc.timeout_sec}s`;
      else if (!parsed.json) failure = `stdout is not JSON (exit ${exec.code}): ${(exec.stderr || exec.stdout).slice(0, 300)}`;
      else if (parsed.json.is_error) failure = `claude reported is_error: ${String(parsed.json.result).slice(0, 300)}`;
      else if (parsed.json.structured_output === undefined) failure = `no structured_output (stop_reason=${parsed.json.stop_reason})`;
      else {
        const v = validate(req.schemaName, parsed.json.structured_output);
        if (!v.ok) {
          failure = `schema violation: ${v.errors.slice(0, 5).join('; ')}`;
          prompt = `${req.prompt}\n\n---\n이전 시도의 출력이 스키마를 위반했다. 아래 오류를 고쳐 다시 출력하라:\n${v.errors.slice(0, 10).map((e) => `- ${e}`).join('\n')}\n`;
        }
      }

      entry.status = failure ? 'FAILED' : 'SUCCESS';
      if (failure) entry.error = redactSecrets(failure);
      await appendFile(logFile, JSON.stringify(entry) + '\n', 'utf8');

      if (!failure && parsed.json) {
        return { ok: true, output: parsed.json.structured_output, costUsd: totalCost, sessionId: parsed.json.session_id ?? '', durationMs: Date.now() - started, attempts: attempt };
      }
      lastError = failure ?? 'unknown';
      // 사용량 한도·속도 제한은 곧바로 재시도해도 같은 결과다. 즉시 실패로 돌려 호출자가 실행을 멈추고 나중에 resume하게 한다.
      if (isQuotaError(failure)) return { ok: false, error: `QUOTA: ${failure}`, attempts: attempt, costUsd: totalCost };
      if (attempt < maxAttempts) {
        const wait = this.backoffMs[Math.min(attempt - 1, this.backoffMs.length - 1)] ?? 0;
        if (wait > 0) await new Promise((r) => setTimeout(r, wait));
      }
    }
    return { ok: false, error: lastError, attempts: maxAttempts, costUsd: totalCost };
  }

  private spawnOnce(prompt: string, req: ClaudeRequest, timeoutMs: number): Promise<{ code: number | null; stdout: string; stderr: string; timedOut: boolean }> {
    return new Promise((resolve) => {
      const child = spawn(this.bin, this.buildArgs(req), { cwd: req.cwd, env: this.env, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
      let stdout = '';
      let stderr = '';
      let timedOut = false;
      const timer = setTimeout(() => {
        timedOut = true;
        child.kill();
      }, timeoutMs);
      child.stdout.setEncoding('utf8').on('data', (d: string) => { stdout += d; });
      child.stderr.setEncoding('utf8').on('data', (d: string) => { stderr += d; });
      child.on('error', (err) => {
        clearTimeout(timer);
        resolve({ code: null, stdout, stderr: `${stderr}\nspawn error: ${err.message}`, timedOut });
      });
      child.on('close', (code) => {
        clearTimeout(timer);
        resolve({ code, stdout, stderr, timedOut });
      });
      child.stdin.on('error', () => { /* 자식이 먼저 죽으면 EPIPE — close에서 처리 */ });
      child.stdin.end(prompt);
    });
  }
}

const QUOTA_RE = /session limit|usage limit|rate limit|rate_limit|quota|overloaded|too many requests|\b429\b|resets (at|in) /i;

/** 사용량 한도·속도 제한 계열 오류인가(재시도 무의미, 실행 중단 후 resume 대상). */
export function isQuotaError(message: string | null | undefined): boolean {
  return !!message && QUOTA_RE.test(message);
}

export function parseCliOutput(stdout: string): { json: CliJson | null } {
  const trimmed = stdout.trim();
  if (!trimmed) return { json: null };
  try {
    return { json: JSON.parse(trimmed) as CliJson };
  } catch {
    // 앞뒤에 잡음이 섞였을 때 마지막 JSON 객체만 살린다.
    const start = trimmed.indexOf('{');
    const end = trimmed.lastIndexOf('}');
    if (start >= 0 && end > start) {
      try { return { json: JSON.parse(trimmed.slice(start, end + 1)) as CliJson }; } catch { /* fallthrough */ }
    }
    return { json: null };
  }
}

/** 함수(호출마다 계산) · 배열(순서대로 소비, 마지막 값 반복) · 그 외 값(항상 같은 출력). Error 인스턴스는 실패. */
type FakeHandler = unknown;

/**
 * 테스트용 러너. 핸들러는 `req.id` → `req.phase` → `'*'` 순으로 찾는다.
 * 배열이면 호출 순서대로 하나씩 꺼내고, Error 인스턴스면 실패로 돌려준다.
 */
export class FakeClaudeRunner implements ClaudeRunner {
  readonly calls: ClaudeRequest[] = [];
  private readonly counters = new Map<string, number>();

  constructor(private readonly handlers: Record<string, FakeHandler>) {}

  async run(req: ClaudeRequest): Promise<ClaudeResult> {
    this.calls.push(req);
    const key = req.id in this.handlers ? req.id : req.phase in this.handlers ? req.phase : '*';
    const handler = this.handlers[key];
    if (handler === undefined) return { ok: false, error: `FakeClaudeRunner: no handler for ${req.id}/${req.phase}`, attempts: 1, costUsd: 0 };
    const n = this.counters.get(key) ?? 0;
    this.counters.set(key, n + 1);
    let out: unknown;
    try {
      out = typeof handler === 'function' ? handler(req, n) : Array.isArray(handler) ? handler[Math.min(n, handler.length - 1)] : handler;
    } catch (e) {
      return { ok: false, error: (e as Error).message, attempts: 1, costUsd: 0 };
    }
    if (out instanceof Error) return { ok: false, error: out.message, attempts: 1, costUsd: 0 };
    const v = validate(req.schemaName, out);
    if (!v.ok) return { ok: false, error: `fake output violates schema ${req.schemaName}: ${v.errors.slice(0, 5).join('; ')}`, attempts: 1, costUsd: 0 };
    return { ok: true, output: out, costUsd: 0.01, sessionId: `fake-${this.calls.length}`, durationMs: 1, attempts: 1 };
  }
}
