import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { mkdtemp } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import type { HarnessPaths } from '../../src/config.js';
import { Run } from '../../src/run.js';

async function tempPaths(): Promise<HarnessPaths> {
  const base = await mkdtemp(path.join(os.tmpdir(), 'dh-run-'));
  const workspace = path.join(base, 'workspace');
  return {
    harnessDir: base, projectRoot: base, workspace,
    current: path.join(workspace, 'current'), runs: path.join(workspace, 'runs'), inbox: path.join(workspace, 'inbox'),
    docsOut: path.join(base, 'docs', 'generated'), prompts: base, schemas: base, templates: base,
  };
}

describe('Run', () => {
  it('번호가 이어지고 RUNNING 상태로 시작한다', async () => {
    const paths = await tempPaths();
    const r1 = await Run.create(paths, 'INITIAL', 'fp1');
    const r2 = await Run.create(paths, 'INCREMENTAL', 'fp2');
    expect(r1.name).toBe('run-0001');
    expect(r2.name).toBe('run-0002');
    expect(r2.state.status).toBe('RUNNING');
    expect(existsSync(r2.staging.current)).toBe(true);
  });

  it('runItem은 예외를 FAILED로 기록하고, 같은 해시의 SUCCESS는 fn을 건너뛴다', async () => {
    const paths = await tempPaths();
    const run = await Run.create(paths, 'INITIAL', 'fp');
    await expect(run.runItem('a', 'h1', async () => { throw new Error('boom'); })).rejects.toThrow('boom');
    expect(run.item('a')).toBe('FAILED');
    expect(run.state.items.a.error).toBe('boom');

    let calls = 0;
    const v1 = await run.runItem('b', 'h1', async (report) => { calls++; report(0.5, 2); return 'first'; }, async () => 'loaded');
    const v2 = await run.runItem('b', 'h1', async () => { calls++; return 'second'; }, async () => 'loaded');
    const v3 = await run.runItem('b', 'h2', async () => { calls++; return 'third'; }, async () => 'loaded');
    expect([v1, v2, v3]).toEqual(['first', 'loaded', 'third']);
    expect(calls).toBe(2);
    expect(run.state.items.b.costUsd).toBe(0.5);
    expect(run.state.items.b.attempts).toBe(2);
    const persisted = JSON.parse(readFileSync(run.stateFile, 'utf8'));
    expect(persisted.items.b.status).toBe('SUCCESS');
  });

  it('commit은 staging을 정본으로 옮기고 prev·staging을 정리하며 사람이 만든 문서를 보존한다', async () => {
    const paths = await tempPaths();
    mkdirSync(paths.current, { recursive: true });
    writeFileSync(path.join(paths.current, 'old.json'), '{"old":true}');
    mkdirSync(paths.docsOut, { recursive: true });
    writeFileSync(path.join(paths.docsOut, 'human.md'), 'keep me');
    writeFileSync(path.join(paths.docsOut, 'to-delete.md'), 'bye');

    const run = await Run.create(paths, 'INITIAL', 'fp');
    writeFileSync(path.join(run.staging.current, 'inventory.json'), '{"x":1}');
    mkdirSync(path.join(run.staging.docs, 'features'), { recursive: true });
    writeFileSync(path.join(run.staging.docs, 'README.md'), 'new readme');
    writeFileSync(path.join(run.staging.docs, 'features', 'F001_X.md'), 'f1');
    writeFileSync(path.join(run.staging.root, 'baseline.json'), '{"documentationVersion":1}');
    await run.commit({ deletedDocuments: ['to-delete.md'] });

    expect(existsSync(path.join(paths.current, 'inventory.json'))).toBe(true);
    expect(existsSync(path.join(paths.current, 'old.json'))).toBe(false);
    expect(existsSync(`${paths.current}.prev`)).toBe(false);
    expect(readFileSync(path.join(paths.docsOut, 'README.md'), 'utf8')).toBe('new readme');
    expect(readFileSync(path.join(paths.docsOut, 'features', 'F001_X.md'), 'utf8')).toBe('f1');
    expect(readFileSync(path.join(paths.docsOut, 'human.md'), 'utf8')).toBe('keep me');
    expect(existsSync(path.join(paths.docsOut, 'to-delete.md'))).toBe(false);
    expect(existsSync(path.join(paths.workspace, 'baseline.json'))).toBe(true);
    expect(run.state.status).toBe('SUCCESS');
  });

  it('recover는 current가 없으면 prev를 되돌리고, 둘 다 있으면 prev를 지운다', async () => {
    const paths = await tempPaths();
    mkdirSync(paths.current, { recursive: true });
    writeFileSync(path.join(paths.current, 'a.json'), '1');
    renameSync(paths.current, `${paths.current}.prev`);
    expect(await Run.recover(paths)).toBe('rolled-back');
    expect(existsSync(path.join(paths.current, 'a.json'))).toBe(true);

    mkdirSync(`${paths.current}.prev`, { recursive: true });
    expect(await Run.recover(paths)).toBe('rolled-forward');
    expect(existsSync(`${paths.current}.prev`)).toBe(false);
    expect(await Run.recover(paths)).toBe('none');
  });

  it('latestIncomplete는 RUNNING인 최신 Run만 돌려준다', async () => {
    const paths = await tempPaths();
    expect(await Run.latestIncomplete(paths)).toBeNull();
    const r1 = await Run.create(paths, 'INITIAL', 'fp');
    expect((await Run.latestIncomplete(paths))?.name).toBe('run-0001');
    await r1.fail('x');
    expect(await Run.latestIncomplete(paths)).toBeNull();
    const r2 = await Run.create(paths, 'INCREMENTAL', 'fp');
    await r2.abandon('newer changes');
    expect(await Run.latestIncomplete(paths)).toBeNull();
    const reopened = await Run.open(paths, 'run-0002');
    expect(reopened.state.abandonReason).toBe('newer changes');
  });
});
