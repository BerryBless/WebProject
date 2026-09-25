import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { defaultHarnessDir } from '../../src/config.js';
import { listSchemaNames, loadSchema, validate, type SchemaName } from '../../src/schema.js';

const fixture = (name: string) => JSON.parse(readFileSync(path.join(defaultHarnessDir(), 'test', 'fixtures', 'schema', name), 'utf8'));

describe('schema', () => {
  it('정상 기능 분석 픽스처를 통과시킨다', () => {
    expect(validate('feature', fixture('feature.ok.json'))).toEqual({ ok: true });
  });

  it('필수 근거가 빠지면 실패하고 경로를 알려준다', () => {
    const bad = fixture('feature.ok.json');
    bad.evidence = [];
    const r = validate('feature', bad);
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.errors.join('\n')).toContain('/evidence');
  });

  it('status 오타는 실패한다', () => {
    const bad = fixture('feature.ok.json');
    bad.entryPoints[0].status = 'CONFIRM';
    expect(validate('feature', bad).ok).toBe(false);
  });

  it('모든 스키마가 $ref 없이 인라인되고 컴파일된다', () => {
    for (const name of listSchemaNames()) {
      const s = loadSchema(name as SchemaName);
      expect(JSON.stringify(s)).not.toContain('$ref');
      expect(validate(name as SchemaName, {}).ok).toBe(false);
    }
    expect(listSchemaNames().length).toBeGreaterThanOrEqual(16);
  });
});
