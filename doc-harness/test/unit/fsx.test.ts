import { existsSync, mkdirSync, readdirSync, writeFileSync } from 'node:fs';
import { mkdtemp } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { loadConfig } from '../../src/config.js';
import { atomicWriteJson, isSecretPath, listSourceFiles, readJson, sha256 } from '../../src/fsx.js';

describe('fsx', () => {
  it('sha256은 고정값을 낸다', () => {
    expect(sha256('a')).toBe('ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb');
  });

  it('atomicWriteJson은 tmp 파일을 남기지 않고 다시 읽힌다', async () => {
    const dir = await mkdtemp(path.join(os.tmpdir(), 'dh-fsx-'));
    const file = path.join(dir, 'nested', 'a.json');
    await atomicWriteJson(file, { x: 1 });
    expect(await readJson<{ x: number }>(file)).toEqual({ x: 1 });
    expect(readdirSync(path.dirname(file)).filter((f) => f.endsWith('.tmp'))).toHaveLength(0);
  });

  it('listSourceFiles는 제외 규칙을 적용하고 /로 정규화된 정렬 목록을 준다', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'dh-list-'));
    for (const f of ['Api/Program.cs', 'Api/bin/Debug/x.dll', 'Api/obj/y.cs', 'Web/node_modules/m/index.ts', 'Web/src/App.tsx', 'doc-harness/src/x.ts', 'README.md', 'image.jpg']) {
      mkdirSync(path.dirname(path.join(root, f)), { recursive: true });
      writeFileSync(path.join(root, f), 'x');
    }
    const cfg = loadConfig();
    const files = listSourceFiles(root, cfg);
    expect(files).toEqual(['Api/Program.cs', 'README.md', 'Web/src/App.tsx']);
    expect(existsSync(path.join(root, 'Api/obj/y.cs'))).toBe(true);
  });

  it('비밀 파일 경로를 알아본다', () => {
    const cfg = loadConfig();
    expect(isSecretPath('PortfolioBlog.Api/appsettings.Development.json', cfg)).toBe(true);
    expect(isSecretPath('deploy/.env.example', cfg)).toBe(true);
    expect(isSecretPath('PortfolioBlog.Api/Program.cs', cfg)).toBe(false);
  });
});
