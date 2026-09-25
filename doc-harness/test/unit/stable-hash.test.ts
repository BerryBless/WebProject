import { describe, expect, it } from 'vitest';
import { today } from '../../src/fsx.js';
import { stableInputHash } from '../../src/phases/common.js';

describe('stableInputHash', () => {
  it('오늘 날짜가 프롬프트에 있어도 해시는 날짜와 무관하다', () => {
    const a = stableInputHash(`오늘 날짜: ${today()}. 본문 (plan/2026-09-23-x.md)`, 'x');
    const b = stableInputHash('오늘 날짜: 2026-09-23. 본문 (plan/2026-09-23-x.md)', 'x');
    const c = stableInputHash('오늘 날짜: 2026-09-23. 본문 (plan/2026-09-24-x.md)', 'x');
    expect(a).toBe(b);
    expect(a).toBe(c);
    expect(stableInputHash('본문', 'x')).not.toBe(stableInputHash('본문', 'y'));
    expect(stableInputHash('본문 A', 'x')).not.toBe(stableInputHash('본문 B', 'x'));
  });
});
