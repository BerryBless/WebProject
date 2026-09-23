import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { loadBaseline } from '../../src/baseline.js';
import { FakeClaudeRunner, type ClaudeRequest } from '../../src/claude.js';
import { loadConfig } from '../../src/config.js';
import { decideMode } from '../../src/mode.js';
import { runPipeline, statusText, verifyOnly } from '../../src/pipeline.js';
import { Run } from '../../src/run.js';
import type { RunRecord } from '../../src/types.js';
import { testPaths } from '../helpers/context.js';
import { fakeResponder, tempMiniRepo } from '../helpers/fake-pipeline.js';
import { sampleBaseline } from '../helpers/samples.js';

describe('decideMode', () => {
  const cs = (n: number) => ({ fingerprint: 'fp', baselineCommit: 'abc', headCommit: 'abc', files: Array.from({ length: n }, (_, i) => ({ file: `f${i}`, changeType: 'MODIFIED' as const, untracked: false, addedLines: [], removedLines: [], relatedSymbols: [], possibleFeatures: [], requiresAnalysis: true })) });
  it('baseline 없음/커밋 없음/--full → INITIAL, 변경 없음 → UP_TO_DATE, 그 외 INCREMENTAL', () => {
    expect(decideMode(null, false, cs(0), { full: false }).mode).toBe('INITIAL');
    expect(decideMode(sampleBaseline(), true, cs(0), { full: true }).mode).toBe('INITIAL');
    expect(decideMode(sampleBaseline(), true, { ...cs(1), baselineCommit: null }, { full: false }).mode).toBe('INITIAL');
    expect(decideMode(sampleBaseline(), false, cs(1), { full: false }).mode).toBe('INITIAL');
    expect(decideMode(sampleBaseline(), true, cs(0), { full: false }).mode).toBe('UP_TO_DATE');
    expect(decideMode(sampleBaseline(), true, cs(3), { full: false }).mode).toBe('INCREMENTAL');
  });
});

describe('runPipeline (Fake, mini-project)', () => {
  it('INITIAL → baseline 생성 → 변경 없음 UP_TO_DATE → 파일 수정 INCREMENTAL(해당 기능만) → 신규 파일이면 신규 기능 → verify는 baseline을 바꾸지 않는다', async () => {
    const root = await tempMiniRepo();
    const paths = await testPaths(root);
    const cfg = loadConfig();
    const state = { features: (await import('../helpers/samples.js')).sampleFeatures().features, newFeatureFiles: [] as string[] };
    const fake = new FakeClaudeRunner({ '*': fakeResponder(state) });
    const logs: string[] = [];
    const log = (m: string) => logs.push(m);

    // 1) INITIAL
    const r1 = await runPipeline({ mode: 'auto', cfg, paths, runner: fake, log });
    expect(r1.status).toBe('SUCCESS');
    expect(r1.mode).toBe('INITIAL');
    expect(r1.report).toContain('Documentation Complete');
    const b1 = (await loadBaseline(paths))!;
    expect(b1.documentationVersion).toBe(1);
    expect(b1.baselineCommit).toHaveLength(40);
    expect(Object.keys(b1.files)).toContain('Api/Features/Posts/PostEndpoints.cs');
    expect(b1.files['Api/Features/Posts/PostEndpoints.cs'].features).toEqual(['F001']);
    expect(existsSync(path.join(paths.docsOut, 'README.md'))).toBe(true);
    expect(existsSync(path.join(paths.docsOut, 'features', 'F001_POST_LIST.md'))).toBe(true);
    expect(existsSync(path.join(paths.docsOut, '20_CHANGELOG.md'))).toBe(true);
    expect(existsSync(path.join(paths.current, 'features', 'F002.json'))).toBe(true);
    expect(existsSync(path.join(paths.workspace, 'depgraph.json'))).toBe(true);
    const rec1 = JSON.parse(readFileSync(path.join(paths.runs, 'run-0001', 'run.json'), 'utf8')) as RunRecord;
    expect(rec1.status).toBe('SUCCESS');
    expect(rec1.claudeCalls).toBeGreaterThan(10);
    expect(fake.calls.filter((c) => c.id.startsWith('feature:'))).toHaveLength(2);

    // 2) 변경 없음
    const r2 = await runPipeline({ mode: 'auto', cfg, paths, runner: fake, log });
    expect(r2.status).toBe('UP_TO_DATE');
    expect(r2.report).toContain('문서화 확인 완료');
    expect((await loadBaseline(paths))!.documentationVersion).toBe(1);

    // 3) F001 파일 수정 → INCREMENTAL, F001(+의존 없음)만 재분석
    writeFileSync(path.join(root, 'Api/Features/Posts/PostEndpoints.cs'), readFileSync(path.join(root, 'Api/Features/Posts/PostEndpoints.cs'), 'utf8') + '\n// changed\n');
    const before = fake.calls.length;
    const r3 = await runPipeline({ mode: 'auto', cfg, paths, runner: fake, log });
    expect(r3.status).toBe('SUCCESS');
    expect(r3.mode).toBe('INCREMENTAL');
    expect(r3.report).toContain('문서화 완료');
    const calls3 = fake.calls.slice(before).map((c) => c.id);
    expect(calls3).toContain('classify');
    expect(calls3).toContain('feature-delta');
    expect(calls3).toContain('feature:F001');
    expect(calls3).not.toContain('feature:F002');
    expect(calls3).not.toContain('inventory');
    const b3 = (await loadBaseline(paths))!;
    expect(b3.documentationVersion).toBe(2);
    expect(r3.record!.affectedFeatures).toEqual(['F001']);
    expect(readFileSync(path.join(paths.docsOut, '20_CHANGELOG.md'), 'utf8')).toContain('증분 변경');

    // 4) 신규 파일 → 신규 기능 F003
    mkdirSync(path.join(root, 'Api/Features/Search'), { recursive: true });
    // 엔드포인트를 선언하면 Fake api.json에 없어 MISSING_ENDPOINT로 정당하게 막힌다 → 엔드포인트 없는 새 클래스로 신규 기능만 흉내 낸다.
    writeFileSync(path.join(root, 'Api/Features/Search/SearchEndpoints.cs'), 'public static class SearchEndpoints { public static int Count() => 1; }\n');
    state.newFeatureFiles = ['Api/Features/Search/SearchEndpoints.cs'];
    const r4 = await runPipeline({ mode: 'auto', cfg, paths, runner: fake, log });
    expect(r4.status).toBe('SUCCESS');
    expect(r4.record!.newFeatures).toEqual(['F003']);
    expect(existsSync(path.join(paths.docsOut, 'features', 'F003_NEW_0.md'))).toBe(true);
    expect((await loadBaseline(paths))!.features.F003.status).toBe('ACTIVE');

    // 5) verify-only: baseline 불변, Run은 VERIFY_ONLY
    const v = await verifyOnly({ cfg, paths, runner: fake, log, llm: false });
    expect(v.report).toContain('문서화 검증 완료');
    expect((await loadBaseline(paths))!.documentationVersion).toBe(3);
    const latest = await Run.latest(paths);
    expect(latest!.state.mode).toBe('VERIFY_ONLY');

    // 6) status
    const s = await statusText({ cfg, paths });
    expect(s).toContain('Baseline: 버전 3');
    expect(s).toContain('UP_TO_DATE');
  }, 120_000);

  it('검증이 계속 실패하면 Run FAILED, baseline·문서 불변, 리포트에 남은 문제', async () => {
    const root = await tempMiniRepo();
    const paths = await testPaths(root);
    const cfg = loadConfig();
    const base = fakeResponder();
    const fake = new FakeClaudeRunner({ '*': (req: ClaudeRequest) => req.schemaName === 'verification' && req.id.startsWith('verify:features') ? { ...(base(req) as object), hallucinations: [{ document: 'features/F001_POST_LIST.md', type: 'GHOST', description: '없는 메서드', evidence: [] }] } : base(req) });
    const r = await runPipeline({ mode: 'auto', cfg, paths, runner: fake, log: () => undefined });
    expect(r.status).toBe('FAILED');
    expect(r.report).toContain('문서화 실패');
    expect(r.report).toContain('없는 메서드');
    expect(await loadBaseline(paths)).toBeNull();
    expect(existsSync(path.join(paths.docsOut, 'README.md'))).toBe(false);
    expect(existsSync(path.join(paths.runs, 'run-0001', 'staging', 'docs', 'README.md'))).toBe(false);
    expect((await Run.latest(paths))!.state.status).toBe('FAILED');
    expect(fake.calls.filter((c) => c.id.startsWith('feature:F001:fix'))).toHaveLength(2);
  }, 120_000);

  it('--phase discovery는 RUNNING Run을 남기고 resume이 이어간다', async () => {
    const root = await tempMiniRepo();
    const paths = await testPaths(root);
    const cfg = loadConfig();
    const fake = new FakeClaudeRunner({ '*': fakeResponder() });
    const p = await runPipeline({ mode: 'auto', cfg, paths, runner: fake, log: () => undefined, phase: 'discovery' });
    expect(p.status).toBe('PARTIAL');
    expect((await Run.latestIncomplete(paths))!.name).toBe('run-0001');
    expect(fake.calls.map((c) => c.id)).toEqual(['inventory', 'architecture', 'discovery']);
    const r = await runPipeline({ mode: 'auto', cfg, paths, runner: fake, log: () => undefined });
    expect(r.status).toBe('SUCCESS');
    expect(r.runName).toBe('run-0001');
    expect(fake.calls.filter((c) => c.id === 'inventory')).toHaveLength(1);
  }, 120_000);
});
