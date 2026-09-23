import { describe, expect, it } from 'vitest';
import { FakeClaudeRunner } from '../../src/claude.js';
import { featureDocName } from '../../src/docs-index.js';
import type { Journal } from '../../src/phases/failures.js';
import { updateDocs, inputsHash, readDocs } from '../../src/render/documents.js';
import { extractMermaidBlocks, parseSections, replaceSection } from '../../src/render/sections.js';
import { renderChangelog, renderFailureHistory, renderFeatureDoc, renderTemplateDoc } from '../../src/render/templates.js';
import { makeTestContext } from '../helpers/context.js';
import { sampleBaseline, sampleDiagram, sampleFeatureAnalysis } from '../helpers/samples.js';
import { sampleWorkspace } from './depgraph.test.js';

const journal = (): Journal => ({ troubleshooting: [{ id: 'TS001', symptom: '로그인 무제한', cause: '속도 제한 없음', howToConfirm: ['반복 시도'], resolution: ['제한 추가'], relatedCode: ['Api/Features/Auth/AuthEndpoints.cs'], status: 'CONFIRMED', addedAt: '2026-09-23' }], changelog: [{ date: '2026-09-23', run: 'run-0001', title: '최초 문서화', classification: ['DOCUMENTATION_ONLY'], description: 'd', impact: [], relatedDocs: ['README.md'], commits: [], significance: 'MAJOR' }] });

const docOutput = (title: string, ids: string[]) => ({ title, oneLiner: `${title} 한 줄`, sections: ids.map((id) => ({ id, heading: id, body: `${id} 본문`, diagrams: [] as never[], unchanged: false })), relatedDocs: [], unknowns: [] });

describe('templates', () => {
  it('기능 문서는 결론→흐름→다이어그램(코드 근거 표)→데이터→실패→코드→관련 순서로 섹션 앵커를 가진다', () => {
    const fa = sampleFeatureAnalysis();
    fa.history = [{ date: '2026-09-23', title: '흐름 변경', classification: ['FEATURE_CHANGE'], before: 'a', after: 'b', reason: { text: 'r', status: 'CONFIRMED', evidence: [{ file: 'x.cs' }] }, impact: ['i'], significance: 'MAJOR', commits: ['abc'] }, { date: '2026-09-23', title: '사소', classification: ['REFACTOR'], before: 'a', after: 'b', reason: { text: 'r', status: 'CONFIRMED', evidence: [] }, impact: [], significance: 'TRIVIAL', commits: [] }];
    const { name, md } = renderFeatureDoc(fa);
    expect(name).toBe('features/F001_POST_LIST.md');
    expect(parseSections(md).sections.map((s) => s.id)).toEqual(['summary', 'flow', 'F001_SEQUENCE', 'data', 'failures', 'code', 'history', 'related']);
    const diagram = parseSections(md).sections[2].body;
    expect(diagram.indexOf('PostEndpoints가 AppDbContext')).toBeLessThan(diagram.indexOf('```mermaid'));
    expect(diagram.indexOf('```mermaid')).toBeLessThan(diagram.indexOf('### 코드 근거'));
    expect(diagram).toContain('| PostEndpoints | `Api/Features/Posts/PostEndpoints.cs` |');
    expect(md).toContain('### 2026-09-23 — 흐름 변경');
    expect(md).not.toContain('사소');
    expect(md).not.toMatch(/classDef|style /);
  });

  it('템플릿 문서들이 스키마 산출물에서 렌더되고 제거 기능·저널이 반영된다', () => {
    const ws = sampleWorkspace();
    ws.features.features[1].status = 'REMOVED';
    const extras = { journal: journal(), prevDocs: new Map<string, string>(), fileTree: 'Api/\n  Program.cs', today: '2026-09-23' };
    const features = renderTemplateDoc('09_FEATURES.md', ws, extras);
    expect(features).toContain('## 제거된 기능');
    expect(features).toContain('[F001](features/F001_POST_LIST.md)');
    expect(renderTemplateDoc('08_API.md', ws, extras)).toContain('| POST | `/api/auth/login` |');
    expect(renderTemplateDoc('07_DATA_MODEL.md', ws, extras)).toContain('erDiagram');
    expect(renderFailureHistory(ws)).toContain('## <a id="fail001"></a>FAIL001');
    expect(renderTemplateDoc('12_TROUBLESHOOTING.md', ws, extras)).toContain('## TS001 로그인 무제한');
    expect(renderChangelog(journal())).toContain('## 2026-09-23');
    expect(renderTemplateDoc('README.md', ws, extras)).toContain('[00_EXECUTIVE_SUMMARY.md]');
    expect(renderTemplateDoc('19_UNKNOWN_AND_TODO.md', ws, extras)).toContain('POTENTIAL_ISSUE');
    expect(renderTemplateDoc('03_DIRECTORY_STRUCTURE.md', ws, extras)).toContain('Program.cs');
    expect(renderTemplateDoc('17_TECH_DEBT.md', ws, extras)).toContain('DEBT001');
  });
});

describe('updateDocs', () => {
  it('ALL: 기능·템플릿·서술 문서를 전부 만들고 서술 문서는 섹션 골격대로 조립한다', async () => {
    const ws = sampleWorkspace();
    const fake = new FakeClaudeRunner({ document: (req: { id: string }) => docOutput(req.id.replace('doc:', ''), ['summary', 'key-points', 'related']) });
    const { ctx } = await makeTestContext(fake);
    const r = await updateDocs(ctx, { ws, journal: journal(), scope: { kind: 'ALL' }, prevDocs: new Map(), baseline: null, changes: null, prevAnalyses: new Map() });
    expect([...r.written.keys()]).toEqual(expect.arrayContaining(['features/F001_POST_LIST.md', '08_API.md', '00_EXECUTIVE_SUMMARY.md', '18_GLOSSARY.md', 'README.md']));
    expect(fake.calls).toHaveLength(12);
    const exec = r.written.get('00_EXECUTIVE_SUMMARY.md')!;
    expect(parseSections(exec).sections.map((s) => s.id)).toEqual(['one-liner', 'summary', 'key-points', 'related']);
    expect(r.diagramDecisions.filter((d) => d.decision === 'NEW').map((d) => d.diagram)).toEqual(expect.arrayContaining(['F001_SEQUENCE', 'DATA_ER']));
    expect(r.deleted).toEqual([]);
  });

  it('PARTIAL: 영향 문서만 만들고, 수동 수정 섹션을 보존하며, 같은 다이어그램은 UNCHANGED, 다르면 I08 판정을 따른다', async () => {
    const ws = sampleWorkspace();
    // 최초 문서 생성
    const fake1 = new FakeClaudeRunner({ document: (req: { id: string }) => docOutput(req.id.replace('doc:', ''), ['summary', 'related']) });
    const { ctx: ctx1 } = await makeTestContext(fake1);
    const first = await updateDocs(ctx1, { ws, journal: journal(), scope: { kind: 'ALL' }, prevDocs: new Map(), baseline: null, changes: null, prevAnalyses: new Map() });
    const prevDocs = new Map(first.written);
    // 사람이 F001 요약 섹션과 F002 다이어그램을 고친다
    const f1 = 'features/F001_POST_LIST.md';
    prevDocs.set(f1, replaceSection(prevDocs.get(f1)!, 'summary', '## 한 줄 요약\n\n사람이 고친 요약').replace(/hash="[0-9a-f]+"(?= -->\n## 한 줄 요약\n\n사람이)/, 'hash="deadbeef"'));
    const f2 = 'features/F002_ADMIN_LOGIN.md';
    prevDocs.set(f2, prevDocs.get(f2)!.replace('PostEndpoints->>AppDbContext: SaveChanges', 'PostEndpoints->>AppDbContext: SaveChanges (manual)'));
    // 새 분석: F001 다이어그램이 바뀌었다(하네스가 I08을 묻는다), F002는 사람이 고친 것과 분석이 같다고 가정하지 않고 → F002는 scope 밖
    const fa1 = ws.featureAnalyses.get('F001')!;
    fa1.diagrams = [{ ...sampleDiagram('F001_SEQUENCE', 'sequence'), mermaid: 'sequenceDiagram\n  participant PostEndpoints\n  participant AppDbContext\n  PostEndpoints->>AppDbContext: SaveChanges\n  AppDbContext-->>PostEndpoints: ok' }];
    const fake2 = new FakeClaudeRunner({
      document: (req: { id: string }) => docOutput(req.id.replace('doc:', ''), ['summary', 'related']),
      'diagram:F001_SEQUENCE': { decision: 'UPDATED', mermaid: 'sequenceDiagram\n  PostEndpoints->>AppDbContext: SaveChanges\n  AppDbContext-->>PostEndpoints: ok', changedEdges: ['AppDbContext-->>PostEndpoints: ok 추가'], nodes: [], reason: '응답 간선 추가' },
    });
    const baseline = sampleBaseline();
    const { ctx: ctx2 } = await makeTestContext(fake2, { mode: 'INCREMENTAL' });
    const r = await updateDocs(ctx2, { ws, journal: journal(), scope: { kind: 'PARTIAL', features: ['F001'], changedFiles: ['Api/Features/Posts/PostEndpoints.cs'], docs: [f1, '08_API.md'], diagrams: ['F001_SEQUENCE'] }, prevDocs, baseline, changes: { fingerprint: 'fp', baselineCommit: 'abc', headCommit: 'def', files: [] }, prevAnalyses: new Map() });
    expect(r.written.has(f1)).toBe(true);
    expect(r.skipped).toContain(f2);
    expect(r.written.get(f1)).toContain('사람이 고친 요약');
    expect(r.manualEdits).toEqual([{ document: f1, section: 'summary', kept: true }]);
    expect(r.diagramDecisions.find((d) => d.diagram === 'F001_SEQUENCE')?.decision).toBe('UPDATED');
    expect(extractMermaidBlocks(r.written.get(f1)!)[0].code).toContain('AppDbContext-->>PostEndpoints: ok');
    // 서술 문서: 입력 해시가 baseline과 다르므로(baseline에 기록 없음) 전부 재생성 대상이지만 scope.docs에 있는 것 + 해시 변경분만. 여기서는 baseline.documents가 비어 있어 모두 changedInputs.
    expect(fake2.calls.filter((c) => c.id.startsWith('doc:')).length).toBeGreaterThan(0);
    // 템플릿 문서 중 내용이 같은 것은 skip
    expect(r.skipped).toContain('17_TECH_DEBT.md');
  });

  it('inputsHash는 선언된 입력만 반영하고 readDocs는 하위 디렉터리를 읽는다', async () => {
    const ws = sampleWorkspace();
    const h1 = inputsHash('08_API.md', ws, journal(), 't');
    ws.inventory.project.name = 'changed';
    expect(inputsHash('08_API.md', ws, journal(), 't')).toBe(h1);
    ws.api.endpoints.pop();
    expect(inputsHash('08_API.md', ws, journal(), 't')).not.toBe(h1);
    const { ctx } = await makeTestContext(new FakeClaudeRunner({}));
    const { mkdirSync, writeFileSync } = await import('node:fs');
    mkdirSync(`${ctx.paths.docsOut}/features`, { recursive: true });
    writeFileSync(`${ctx.paths.docsOut}/README.md`, 'r');
    writeFileSync(`${ctx.paths.docsOut}/features/${featureDocName('F001', 'X').split('/')[1]}`, 'f');
    expect([...readDocs(ctx.paths.docsOut).keys()].sort()).toEqual(['README.md', 'features/F001_X.md']);
  });
});
