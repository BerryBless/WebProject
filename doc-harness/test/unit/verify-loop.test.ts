import { describe, expect, it } from 'vitest';
import { FakeClaudeRunner } from '../../src/claude.js';
import { loadConfig } from '../../src/config.js';
import { truthLists } from '../../src/context.js';
import { renderApiDoc, renderDataDoc, renderFeatureDoc } from '../../src/render/templates.js';
import { groupDocs, relatedUnchangedDocs } from '../../src/verify/llm.js';
import { hasBlocking, issuesByDoc, runVerificationPass, verificationLoop } from '../../src/verify/loop.js';
import { makeTestContext, miniProjectRoot } from '../helpers/context.js';
import { sampleFeatureAnalysis, sampleVerification } from '../helpers/samples.js';
import { sampleWorkspace } from './depgraph.test.js';

const cfg = loadConfig();
const truth = truthLists(miniProjectRoot, cfg);

function baseDocs(ws = sampleWorkspace()): Map<string, string> {
  const docs = new Map<string, string>();
  for (const fa of ws.featureAnalyses.values()) { const { name, md } = renderFeatureDoc(fa); docs.set(name, md); }
  docs.set('08_API.md', renderApiDoc(ws));
  docs.set('07_DATA_MODEL.md', renderDataDoc(ws));
  docs.set('02_ARCHITECTURE.md', '# arch\n\nPostEndpoints, AuthEndpoints, AppDbContext');
  docs.set('09_FEATURES.md', '# f\n\n[F001](features/F001_POST_LIST.md) GET /api/posts POST /api/posts POST /api/auth/login');
  docs.set('11_FAILURE_HISTORY.md', '# fh');
  docs.set('13_SECURITY.md', '# sec');
  return docs;
}

describe('runVerificationPass', () => {
  it('결정적 검사만으로 깨끗한 문서는 통과하고 LLM 이슈는 fixRequired로 정규화된다', async () => {
    const ws = sampleWorkspace();
    const fake = new FakeClaudeRunner({
      'verify:features:1': sampleVerification({ hallucinations: [{ document: 'features/F001_POST_LIST.md', section: 'flow', type: 'NO_SUCH_METHOD', description: 'DoX가 없다', evidence: [{ file: 'Api/Program.cs' }] }] }),
      '*': sampleVerification(),
    });
    const { ctx } = await makeTestContext(fake);
    const v = await runVerificationPass(ctx, { ws, docs: baseDocs(ws), changed: null, truth, iteration: 1, manualKept: new Map() });
    expect(v.mermaidParser).toBe('VERIFIED');
    expect(v.hallucinations).toHaveLength(1);
    expect(v.passed).toBe(false);
    expect(issuesByDoc(v).get('features/F001_POST_LIST.md')![0]).toContain('section=flow');
    expect(fake.calls.map((c) => c.id).sort()).toEqual(['verify:data-api:1', 'verify:features:1', 'verify:operations:1', 'verify:overview:1']);
    expect(fake.calls[0].prompt).toContain('공격적으로 검증하는 리뷰어');
  }, 60_000);

  it('llm=false면 결정적 검사만 하고, 증분이면 변경 문서군 + 일관성 검사를 부른다', async () => {
    const ws = sampleWorkspace();
    const docs = baseDocs(ws);
    docs.set('features/F001_POST_LIST.md', docs.get('features/F001_POST_LIST.md')!.replace('`Api/Features/Posts/PostEndpoints.cs`', '`Api/Features/Posts/Nope.cs`'));
    const fake = new FakeClaudeRunner({ 'consistency:1': { issues: [{ document: '02_ARCHITECTURE.md', type: 'INCONSISTENT', description: '옛 구조', evidence: [] }], summary: '1건' }, '*': sampleVerification() });
    const { ctx } = await makeTestContext(fake, { mode: 'INCREMENTAL' });
    const det = await runVerificationPass(ctx, { ws, docs, changed: null, truth, iteration: 1, manualKept: new Map(), llm: false });
    expect(fake.calls).toHaveLength(0);
    expect(det.hallucinations.map((i) => i.type)).toContain('HALLUCINATED_PATH');
    expect(hasBlocking(det)).toBe(true);
    const inc = await runVerificationPass(ctx, { ws, docs, changed: new Set(['features/F001_POST_LIST.md']), truth, iteration: 1, manualKept: new Map() });
    expect(fake.calls.map((c) => c.id)).toEqual(['verify:features:1', 'consistency:1']);
    expect(inc.incorrectRelations.map((i) => i.document)).toContain('02_ARCHITECTURE.md');
  }, 60_000);
});

describe('verificationLoop', () => {
  it('이슈 → 수정 → 재검증으로 통과하고, 수동 수정 섹션 이슈는 overridden에 남는다', async () => {
    const ws = sampleWorkspace();
    const docs = baseDocs(ws);
    const fake = new FakeClaudeRunner({
      'verify:features:1': sampleVerification({ incorrectRelations: [{ document: 'features/F001_POST_LIST.md', section: 'F001_SEQUENCE', type: 'WRONG_ORDER', description: '순서 오류', evidence: [] }] }),
      '*': sampleVerification(),
    });
    const { ctx } = await makeTestContext(fake);
    const fixerCalls: Map<string, string[]>[] = [];
    const r = await verificationLoop(ctx, {
      ws, docs, changed: null, truth, manualKept: new Map([['features/F001_POST_LIST.md', new Set(['F001_SEQUENCE'])]]), maxIterations: 3,
      fixer: async (issues) => { fixerCalls.push(issues); return { docs: new Map([['features/F001_POST_LIST.md', renderFeatureDoc(sampleFeatureAnalysis()).md]]), overridden: [] }; },
    });
    expect(r.passed).toBe(true);
    expect(r.iterations).toBe(2);
    expect(fixerCalls).toHaveLength(1);
    expect(r.overridden).toEqual([{ document: 'features/F001_POST_LIST.md', section: 'F001_SEQUENCE', reason: expect.stringContaining('순서 오류') }]);
  }, 60_000);

  it('3회 내내 이슈면 passed=false로 끝난다', async () => {
    const ws = sampleWorkspace();
    const fake = new FakeClaudeRunner({ '*': (req: { id: string }) => req.id.startsWith('verify:features') ? sampleVerification({ hallucinations: [{ document: 'features/F001_POST_LIST.md', type: 'X', description: '계속 틀림', evidence: [] }] }) : sampleVerification() });
    const { ctx } = await makeTestContext(fake);
    const r = await verificationLoop(ctx, { ws, docs: baseDocs(ws), changed: null, truth, manualKept: new Map(), maxIterations: 3, fixer: async () => ({ docs: new Map(), overridden: [] }) });
    expect(r.passed).toBe(false);
    expect(r.iterations).toBe(3);
    expect(r.history).toHaveLength(3);
  }, 60_000);
});

describe('groupDocs / relatedUnchangedDocs', () => {
  it('문서를 군으로 나누고 큰 군은 조각내며 변경 문서만 고른다', () => {
    const docs = new Map<string, string>([['README.md', 'r'], ['02_ARCHITECTURE.md', 'a'], ['08_API.md', 'x'.repeat(100_000)], ['07_DATA_MODEL.md', 'y'.repeat(100_000)], ['features/F001_A.md', 'f'], ['13_SECURITY.md', 's']]);
    const all = groupDocs(docs, null);
    expect(all.map((g) => g.name)).toEqual(['overview', 'data-api', 'data-api-2', 'features', 'operations']);
    const only = groupDocs(docs, new Set(['features/F001_A.md']));
    expect(only.map((g) => [g.name, [...g.docs.keys()]])).toEqual([['features', ['features/F001_A.md']]]);
    const related = relatedUnchangedDocs(new Map([['features/F001_A.md', '[api](../08_API.md) F001']]), new Map([...docs, ['features/F001_B.md', 'b']]));
    // 100k 문서는 상한(100k)을 넘겨 빠지고, 작은 문서는 항상 남는다.
    expect([...related.keys()].sort()).toEqual(['02_ARCHITECTURE.md', 'features/F001_B.md']);
  });
});
