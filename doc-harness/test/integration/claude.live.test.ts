// 실제 claude CLI를 부른다. DOC_HARNESS_LIVE=1 일 때만 실행된다(vitest.config.ts).
import { existsSync } from 'node:fs';
import { mkdtemp } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { CliClaudeRunner } from '../../src/claude.js';
import { loadConfig, resolvePaths } from '../../src/config.js';

describe('claude live', () => {
  it('구조화 출력을 돌려주고 프로젝트 훅(자동 커밋 메시지 파일)을 건드리지 않는다', async () => {
    const cfg = loadConfig();
    const paths = resolvePaths(cfg);
    const logDir = await mkdtemp(path.join(os.tmpdir(), 'dh-live-'));
    const runner = new CliClaudeRunner(cfg);
    const r = await runner.run({
      id: 'live-smoke', phase: 'inventory', schemaName: 'consistency', cwd: paths.projectRoot, logDir,
      prompt: '도구를 쓰지 말고 issues는 빈 배열, summary는 "live-ok"로 답하라.',
    });
    expect(r.ok).toBe(true);
    if (r.ok) expect((r.output as { summary: string }).summary).toContain('live-ok');
    expect(existsSync(path.join(paths.projectRoot, '.git', 'auto_commit_msg.txt'))).toBe(false);
  }, 180_000);
});
