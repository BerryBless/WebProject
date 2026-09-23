import { mkdtemp, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { buildPrompt, readPrompt, substitute } from '../../src/prompts.js';

describe('prompts', () => {
  it('전역 규칙은 근거 우선순위와 CLAUDE.md 무시 규칙을 담는다', () => {
    const rules = readPrompt('00_global_rules');
    expect(rules).toContain('실제 코드 / 설정');
    expect(rules).toContain('CLAUDE.md');
    expect(rules).toContain('UNKNOWN');
  });

  it('buildPrompt는 규칙으로 시작하고 변수를 치환하며 꼬리를 붙인다', async () => {
    const dir = await mkdtemp(path.join(os.tmpdir(), 'dh-prompts-'));
    await writeFile(path.join(dir, '00_global_rules.md'), '# RULES\n');
    await writeFile(path.join(dir, 'x.md'), 'hello {{name}}\n');
    const p = buildPrompt('x', { name: 'world' }, { promptsDir: dir, tail: 'TAIL' });
    expect(p.startsWith('# RULES')).toBe(true);
    expect(p).toContain('hello world');
    expect(p.trimEnd().endsWith('TAIL')).toBe(true);
  });

  it('치환되지 않은 변수가 남으면 throw', () => {
    expect(() => substitute('a {{b}}', {})).toThrow('{{b}}');
  });
});
