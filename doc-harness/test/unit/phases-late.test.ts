import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { FakeClaudeRunner } from '../../src/claude.js';
import { loadConfig } from '../../src/config.js';
import type { GitExtract } from '../../src/git-extract.js';
import { runApi, runData } from '../../src/phases/dataApi.js';
import { appendJournal, loadJournal, nextFailureId, runFailuresDelta, runFailuresInitial, seedJournalFromFailures } from '../../src/phases/failures.js';
import { normalizeFeatureAnalysis, runFeatures } from '../../src/phases/feature.js';
import { runOperations } from '../../src/phases/operations.js';
import { makeTestContext } from '../helpers/context.js';
import { sampleApi, sampleArchitecture, sampleClassification, sampleData, sampleFailures, sampleFeatureAnalysis, sampleFeatureSummary, sampleFeatures, sampleOperationsArea } from '../helpers/samples.js';

const emptyExtract: GitExtract = { since: null, commits: [], markers: [{ file: 'Api/Features/Auth/AuthEndpoints.cs', line: 7, text: '// TODO: rate limit' }], workingTreeDiffExcerpt: '' };

describe('phase 04 features', () => {
  it('기능마다 독립 호출, 하나가 두 번 실패해도 나머지는 계속되고 실패 목록을 돌려준다', async () => {
    const feats = sampleFeatures().features;
    const fake = new FakeClaudeRunner({
      'feature:F001': [new Error('busy'), sampleFeatureAnalysis(feats[0])],
      'feature:F002': sampleFeatureAnalysis(feats[1]),
    });
    const { ctx } = await makeTestContext(fake);
    const r1 = await runFeatures(ctx, feats, sampleArchitecture(), new Map(), null);
    expect(r1.failed).toEqual([{ id: 'F001', error: expect.stringContaining('busy') }]);
    expect(r1.analyses.has('F002')).toBe(true);
    expect(fake.calls[0].prompt).toContain('- Api/Features/Posts/PostEndpoints.cs');
    expect(fake.calls[0].prompt).toContain('최초 분석');
    expect(existsSync(path.join(ctx.run.staging.current, 'features', 'F002.json'))).toBe(true);
    // resume: 실패 항목만 다시 돈다
    const r2 = await runFeatures(ctx, feats, sampleArchitecture(), new Map(), null);
    expect(r2.failed).toEqual([]);
    expect(fake.calls.filter((c) => c.id === 'feature:F002')).toHaveLength(1);
    expect(fake.calls.filter((c) => c.id === 'feature:F001')).toHaveLength(2);
  });

  it('병렬 2에서도 id별 결과가 정확하다', async () => {
    const feats = sampleFeatures().features;
    const fake = new FakeClaudeRunner({ '*': (req: { id: string }) => sampleFeatureAnalysis(feats.find((f) => `feature:${f.id}` === req.id)!) });
    const cfg = loadConfig();
    cfg.analysis.feature_parallelism = 2;
    const { ctx } = await makeTestContext(fake, { cfg });
    const r = await runFeatures(ctx, feats, sampleArchitecture(), new Map(), null);
    expect([...r.analyses.keys()].sort()).toEqual(['F001', 'F002']);
    expect(r.analyses.get('F002')!.feature.id).toBe('F002');
  });

  it('normalize는 다이어그램 id 접두사를 맞추고 이전 history를 보존한다', () => {
    const summary = sampleFeatureSummary('F003', 'X');
    const prev = sampleFeatureAnalysis(summary);
    prev.history = [{ date: '2026-09-01', title: '옛 변경', classification: ['REFACTOR'], before: 'a', after: 'b', reason: { text: 'r', status: 'CONFIRMED', evidence: [{ file: 'x' }] }, impact: [], significance: 'MINOR', commits: [] }];
    const fresh = sampleFeatureAnalysis(summary);
    fresh.diagrams[0].id = 'SEQUENCE';
    fresh.history = [{ ...prev.history[0], date: '2026-09-23', title: '새 변경' }];
    const out = normalizeFeatureAnalysis(fresh, summary, prev);
    expect(out.diagrams[0].id).toBe('F003_SEQUENCE');
    expect(out.history.map((h) => h.title)).toEqual(['옛 변경', '새 변경']);
  });
});

describe('phase 05·06·07', () => {
  it('data/api는 DbContext·엔드포인트 파일과 정답 목록을 주입한다', async () => {
    const fake = new FakeClaudeRunner({ data: sampleData(), api: sampleApi() });
    const { ctx } = await makeTestContext(fake);
    const data = await runData(ctx, sampleFeatures().features);
    const api = await runApi(ctx, sampleFeatures().features);
    expect(data.entities.map((e) => e.name)).toEqual(['Post', 'Tag']);
    expect(api.endpoints).toHaveLength(3);
    expect(fake.calls[0].prompt).toContain('- Api/Infrastructure/Data/AppDbContext.cs');
    expect(fake.calls[0].prompt).toContain('- Post  (Api/Infrastructure/Data/AppDbContext.cs)');
    expect(fake.calls[1].prompt).toContain('- Api/Features/Posts/PostEndpoints.cs');
    expect(fake.calls[1].prompt).toContain('POST /api/auth/login');
  });

  it('failures 초기 분석은 git 발췌·마커·문서 경로를 주입하고 id 순으로 정렬한다', async () => {
    const shuffled = sampleFailures();
    shuffled.failures.push({ ...shuffled.failures[0], id: 'FAIL000' });
    const fake = new FakeClaudeRunner({ failures: shuffled });
    const { ctx } = await makeTestContext(fake, { sessionContext: '직전 세션: 로그인 속도 제한 미구현' });
    const out = await runFailuresInitial(ctx, emptyExtract);
    expect(out.failures.map((f) => f.id)).toEqual(['FAIL000', 'FAIL001']);
    const p = fake.calls[0].prompt;
    expect(p).toContain('// TODO: rate limit');
    expect(p).toContain('- docs/architecture.md');
    expect(p).toContain('직전 세션');
    expect(nextFailureId(out.failures)).toBe('FAIL002');
    const journal = await seedJournalFromFailures(ctx, out, 2);
    expect(journal.troubleshooting.map((t) => t.id)).toEqual(['TS000', 'TS001']);
    expect(journal.changelog[0].title).toContain('최초 문서화');
  });

  it('failures 델타는 기존 기록을 유지하고 추가·갱신만 하며 저널에 append한다', async () => {
    const prev = sampleFailures();
    const updated = { ...prev.failures[0], finalSolution: '속도 제한 추가됨' };
    const added = { ...prev.failures[0], id: 'FAIL002', title: '새 실패' };
    const fake = new FakeClaudeRunner({ 'failures-delta': { newFailures: [added], updatedFailures: [updated], troubleshooting: [{ id: 'TS002', symptom: 's', cause: 'c', howToConfirm: ['h'], resolution: ['r'], relatedCode: ['Api/Program.cs'], status: 'CONFIRMED' }], changelog: [{ title: '속도 제한 도입', classification: ['SECURITY_CHANGE'], description: 'd', impact: ['F002'], relatedDocs: ['13_SECURITY.md'], commits: [], significance: 'MAJOR' }, { title: '사소', classification: ['REFACTOR'], description: 'd', impact: [], relatedDocs: [], commits: [], significance: 'TRIVIAL' }], unknowns: ['u1'] } });
    const { ctx } = await makeTestContext(fake, { mode: 'INCREMENTAL' });
    const changes = { fingerprint: 'fp', baselineCommit: null, headCommit: 'h', files: [{ file: 'Api/Features/Auth/AuthEndpoints.cs', changeType: 'MODIFIED' as const, untracked: false, addedLines: ['rate limit'], removedLines: [], relatedSymbols: [], possibleFeatures: [], requiresAnalysis: true }] };
    const { failures, delta } = await runFailuresDelta(ctx, emptyExtract, prev, changes, sampleClassification(['Api/Features/Auth/AuthEndpoints.cs']));
    expect(failures.failures.map((f) => f.id)).toEqual(['FAIL001', 'FAIL002']);
    expect(failures.failures[0].finalSolution).toBe('속도 제한 추가됨');
    expect(failures.unknowns).toEqual(['u1']);
    expect(fake.calls[0].prompt).toContain('FAIL002');
    expect(fake.calls[0].prompt).toContain('- FAIL001 로그인 속도 제한 없음');
    const journal = await appendJournal(ctx, delta);
    expect(journal.changelog.map((c) => c.title)).toEqual(['속도 제한 도입']);
    expect(journal.troubleshooting[0].id).toBe('TS002');
    expect((await loadJournal(ctx)).changelog).toHaveLength(1);
    expect(JSON.parse(readFileSync(path.join(ctx.run.staging.current, 'failures.json'), 'utf8')).failures).toHaveLength(2);
  });

  it('operations는 4개 영역을 따로 호출해 합친다', async () => {
    const fake = new FakeClaudeRunner({ 'operations:errorHandling': sampleOperationsArea('ERR'), 'operations:security': sampleOperationsArea('SEC'), 'operations:performance': sampleOperationsArea('PERF'), 'operations:techDebt': sampleOperationsArea('DEBT') });
    const { ctx } = await makeTestContext(fake);
    const analyses = new Map([['F001', sampleFeatureAnalysis()]]);
    const ops = await runOperations(ctx, analyses, emptyExtract);
    expect(Object.keys(ops).sort()).toEqual(['errorHandling', 'performance', 'security', 'techDebt']);
    expect(fake.calls).toHaveLength(4);
    expect(fake.calls[0].prompt).toContain('[F001] PostEndpoints: DB 연결 실패');
    expect(fake.calls[3].prompt).toContain('// TODO: rate limit');
    expect(fake.calls[0].prompt).not.toContain('// TODO: rate limit');
    expect(existsSync(path.join(ctx.run.staging.current, 'operations.json'))).toBe(true);
  });
});
