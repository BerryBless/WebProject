import { existsSync, readFileSync } from 'node:fs';
import { mkdtemp } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { CliClaudeRunner, FakeClaudeRunner, parseCliOutput, redactSecrets, type ClaudeRequest } from '../../src/claude.js';
import { defaultHarnessDir, loadConfig } from '../../src/config.js';

const fake = path.join(defaultHarnessDir(), 'test', 'fixtures', 'fake-claude.mjs');

async function makeReq(id = 'inventory'): Promise<ClaudeRequest> {
  const logDir = await mkdtemp(path.join(os.tmpdir(), 'dh-claude-'));
  return { id, prompt: '# 테스트 프롬프트\nPassword: ' + 'dummy-' + 'xxxxxxxx', schemaName: 'consistency', phase: 'inventory', cwd: defaultHarnessDir(), logDir };
}

function runner(mode: string, extraEnv: Record<string, string> = {}) {
  const cfg = loadConfig();
  return new CliClaudeRunner(cfg, { bin: process.execPath, prefixArgs: [fake], backoffMs: [0, 0, 0], env: { ...process.env, FAKE_CLAUDE_MODE: mode, ...extraEnv } });
}

describe('CliClaudeRunner', () => {
  it('읽기 전용 고정 인자와 스키마를 넘기고 stdin 프롬프트를 전달한다', async () => {
    const req = await makeReq();
    const r = await runner('ok').run(req);
    expect(r.ok).toBe(true);
    const log = readFileSync(path.join(req.logDir, 'inventory.log'), 'utf8');
    const entry = JSON.parse(log.trim().split('\n')[0]);
    expect(entry.status).toBe('SUCCESS');
    expect(entry.attempts ?? entry.attempt).toBe(1);
    const cli = runner('ok');
    const args = cli.buildArgs(req);
    expect(args).toEqual(expect.arrayContaining(['--tools', 'Read,Glob,Grep', '--setting-sources', 'user', '--no-session-persistence', '--permission-prompts', 'none', '--json-schema']));
    expect(args.join(' ')).not.toMatch(/Write|Edit|Bash/);
    expect(args[args.indexOf('--model') + 1]).toBe('sonnet');
  });

  it('프롬프트 로그는 비밀값을 가린다', async () => {
    const req = await makeReq('secret-check');
    await runner('ok').run(req);
    const promptLog = readFileSync(path.join(req.logDir, 'secret-check.prompt.md'), 'utf8');
    expect(promptLog).not.toContain('xxxxxxxx');
    expect(promptLog).toContain('[REDACTED]');
  });

  it('JSON이 아니면 3회 재시도 후 실패한다', async () => {
    const req = await makeReq('badjson');
    const r = await runner('badjson').run(req);
    expect(r.ok).toBe(false);
    expect(r.attempts).toBe(3);
    const lines = readFileSync(path.join(req.logDir, 'badjson.log'), 'utf8').trim().split('\n');
    expect(lines).toHaveLength(3);
  });

  it('is_error 응답은 실패로 기록한다', async () => {
    const r = await runner('error').run(await makeReq('err'));
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error).toContain('simulated failure');
  });

  it('스키마 위반 출력은 ajv가 잡고 재시도 프롬프트에 오류를 덧붙인다', async () => {
    const stateFile = path.join(await mkdtemp(path.join(os.tmpdir(), 'dh-state-')), 'calls');
    const req = await makeReq('invalid-then-ok');
    req.prompt = 'retry-note-check';
    const r = await runner('invalid_then_ok', { FAKE_CLAUDE_STATE_FILE: stateFile }).run(req);
    expect(r.ok).toBe(true);
    if (r.ok) expect(r.attempts).toBe(2);
    const lines = readFileSync(path.join(req.logDir, 'invalid-then-ok.log'), 'utf8').trim().split('\n').map((l) => JSON.parse(l));
    expect(lines[0].status).toBe('FAILED');
    expect(lines[0].error).toContain('schema violation');
    expect(lines[1].status).toBe('SUCCESS');
  });

  it('사용량 한도 오류는 재시도하지 않고 QUOTA로 즉시 실패한다', async () => {
    const req = await makeReq('limit');
    const r = await runner('limit').run(req);
    expect(r.ok).toBe(false);
    if (!r.ok) { expect(r.error).toMatch(/^QUOTA:/); expect(r.attempts).toBe(1); }
    expect(readFileSync(path.join(req.logDir, 'limit.log'), 'utf8').trim().split('\n')).toHaveLength(1);
  });

  it('없는 실행 파일이면 spawn 오류를 실패로 돌려준다', async () => {
    const cfg = loadConfig();
    const r = await new CliClaudeRunner(cfg, { bin: 'definitely-not-a-real-binary-xyz', backoffMs: [0, 0] }).run(await makeReq('nobin'));
    expect(r.ok).toBe(false);
  });
});

describe('parseCliOutput / redactSecrets', () => {
  it('잡음 섞인 stdout에서 JSON을 건진다', () => {
    expect(parseCliOutput('warning\n{"a":1}\n')?.json).toEqual({ a: 1 });
    expect(parseCliOutput('')?.json).toBeNull();
  });
  it('비밀값 형태를 가린다', () => {
    expect(redactSecrets('Password=' + 'dummyvalue' + ';Host=x')).toBe('Password=[REDACTED];Host=x');
    expect(redactSecrets('AKIA' + 'ABCDEFGHIJKLMNOP')).toBe('[REDACTED]');
  });
});

describe('FakeClaudeRunner', () => {
  it('id → phase → * 순으로 핸들러를 찾고 배열은 순서대로 소비한다', async () => {
    const f = new FakeClaudeRunner({ inventory: [new Error('boom'), { issues: [], summary: 'ok' }], '*': { issues: [], summary: 'star' } });
    const req: ClaudeRequest = { id: 'x', prompt: '', schemaName: 'consistency', phase: 'inventory', cwd: '.', logDir: os.tmpdir() };
    expect((await f.run(req)).ok).toBe(false);
    const second = await f.run(req);
    expect(second.ok && (second.output as { summary: string }).summary).toBe('ok');
    const star = await f.run({ ...req, phase: 'other' });
    expect(star.ok && (star.output as { summary: string }).summary).toBe('star');
    expect(f.calls).toHaveLength(3);
  });
  it('스키마 위반 출력은 실패로 돌려준다', async () => {
    const f = new FakeClaudeRunner({ '*': { nope: 1 } });
    const r = await f.run({ id: 'x', prompt: '', schemaName: 'consistency', phase: 'p', cwd: '.', logDir: os.tmpdir() });
    expect(r.ok).toBe(false);
  });
});
