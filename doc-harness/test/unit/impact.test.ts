import { describe, expect, it } from 'vitest';
import { FakeClaudeRunner } from '../../src/claude.js';
import { classifyChanges } from '../../src/change/classify.js';
import { applyFeatureDelta, nextFeatureIds, runFeatureDelta } from '../../src/change/featureDelta.js';
import { analyzeImpact } from '../../src/change/impact.js';
import { buildDepgraph } from '../../src/depgraph.js';
import type { ChangeSet, ChangedFile, Classification } from '../../src/types.js';
import { makeTestContext } from '../helpers/context.js';
import { sampleClassification, sampleFeatureSummary } from '../helpers/samples.js';
import { sampleWorkspace } from './depgraph.test.js';

const changed = (file: string, changeType: ChangedFile['changeType'] = 'MODIFIED', extra: Partial<ChangedFile> = {}): ChangedFile => ({ file, changeType, untracked: false, addedLines: ['x'], removedLines: [], relatedSymbols: [], possibleFeatures: [], requiresAnalysis: true, ...extra });
const changeSet = (files: ChangedFile[]): ChangeSet => ({ fingerprint: 'fp', baselineCommit: 'abc', headCommit: 'def', files });

describe('impact', () => {
  it('직접 매핑 + 의존 1홉 + 분류별 문서 + 항상 문서', () => {
    const ws = sampleWorkspace();
    const g = buildDepgraph(ws);
    const cs = changeSet([changed('Api/Features/Posts/PostEndpoints.cs')]);
    const cls: Classification = sampleClassification([cs.files[0].file]);
    const impact = analyzeImpact(cs, cls, g, ws.features.features, ws.architecture);
    expect(impact.affectedFeatures).toEqual(['F001', 'F002']);
    expect(impact.widenReason[0]).toContain('의존 1홉');
    expect(impact.affectedDocs).toEqual(expect.arrayContaining(['features/F001_POST_LIST.md', '08_API.md', '00_EXECUTIVE_SUMMARY.md', '20_CHANGELOG.md']));
    expect(impact.affectedDiagrams).toEqual(['ARCH_SYSTEM', 'F001_SEQUENCE', 'F002_SEQUENCE']);
    expect(impact.needsApi).toBe(false);
    expect(impact.candidateNewFeatureFiles).toEqual([]);
  });

  it('변경이 전부 TRIVIAL이면 의존 확장 없이 직접 기능만 대상이다', () => {
    const ws = sampleWorkspace();
    const g = buildDepgraph(ws);
    const cs = changeSet([changed('Api/Features/Posts/PostEndpoints.cs')]);
    const cls: Classification = { items: [{ file: cs.files[0].file, classifications: ['DOCUMENTATION_ONLY'], significance: 'TRIVIAL', possibleFeatures: [], rationale: '주석' }], summary: 's', changelogCandidates: [] };
    const impact = analyzeImpact(cs, cls, g, ws.features.features, ws.architecture);
    expect(impact.affectedFeatures).toEqual(['F001']);
    expect(impact.widenReason.some((w) => w.includes('TRIVIAL'))).toBe(true);
  });

  it('구조 변경이면 같은 컴포넌트의 기능으로 넓히고, 매핑 없는 파일은 신규 후보, 전부 삭제된 기능은 삭제 후보', () => {
    const ws = sampleWorkspace();
    ws.features.features.push({ ...sampleFeatureSummary('F003', 'ORPHAN', ['Api/Features/Posts/Orphan.cs']), dependencies: [] });
    ws.features.features.push({ ...sampleFeatureSummary('F004', 'SIBLING', ['Api/Features/Posts/Other.cs']), dependencies: [] });
    const g = buildDepgraph(ws);
    const cs = changeSet([
      changed('Api/Features/Posts/PostEndpoints.cs'),
      changed('Api/Features/Search/SearchEndpoints.cs', 'UNTRACKED', { untracked: true }),
      changed('Api/Features/Posts/Orphan.cs', 'DELETED', { addedLines: [] }),
      changed('README.md', 'MODIFIED', { requiresAnalysis: false }),
    ]);
    const cls: Classification = { items: [
      { file: 'Api/Features/Posts/PostEndpoints.cs', classifications: ['API_CHANGE'], significance: 'MAJOR', possibleFeatures: ['F001'], rationale: '' },
      { file: 'Api/Features/Search/SearchEndpoints.cs', classifications: ['NEW_FEATURE'], significance: 'MAJOR', possibleFeatures: [], rationale: '' },
      { file: 'Api/Features/Posts/Orphan.cs', classifications: ['REFACTOR'], significance: 'MINOR', possibleFeatures: [], rationale: '' },
      { file: 'README.md', classifications: ['DOCUMENTATION_ONLY'], significance: 'TRIVIAL', possibleFeatures: [], rationale: '' },
    ], summary: 's', changelogCandidates: [] };
    const impact = analyzeImpact(cs, cls, g, ws.features.features, ws.architecture);
    expect(impact.affectedFeatures).toContain('F003');
    expect(impact.affectedFeatures).toContain('F004');
    expect(impact.widenReason.some((w) => w.includes('API_CHANGE') && w.includes('F004'))).toBe(true);
    expect(impact.candidateNewFeatureFiles).toEqual(['Api/Features/Search/SearchEndpoints.cs']);
    expect(impact.removalCandidates).toEqual(['F003']);
    expect(impact.needsApi).toBe(true);
    expect(impact.needsArchitecture).toBe(true);
    expect(impact.affectedDocs).toContain('08_API.md');
  });

  it('classify는 응답에 없는 파일을 UNKNOWN/DOCUMENTATION_ONLY로 보충한다', async () => {
    const fake = new FakeClaudeRunner({ classify: sampleClassification(['Api/Features/Posts/PostEndpoints.cs']) });
    const { ctx } = await makeTestContext(fake, { mode: 'INCREMENTAL' });
    const cs = changeSet([changed('Api/Features/Posts/PostEndpoints.cs'), changed('Api/New.cs', 'UNTRACKED'), changed('docs/x.md', 'MODIFIED', { requiresAnalysis: false })]);
    const out = await classifyChanges(ctx, cs, sampleWorkspace().features.features);
    expect(out.items.map((i) => [i.file, i.classifications[0]])).toEqual([['Api/Features/Posts/PostEndpoints.cs', 'FEATURE_CHANGE'], ['Api/New.cs', 'UNKNOWN'], ['docs/x.md', 'DOCUMENTATION_ONLY']]);
    expect(fake.calls[0].prompt).toContain('### MODIFIED Api/Features/Posts/PostEndpoints.cs');
    expect(fake.calls[0].prompt).toContain('[문서·테스트·도구 전용]');
  });

  it('feature delta는 id를 하네스가 확정하고 applyFeatureDelta가 REMOVED를 보존한다', async () => {
    const ws = sampleWorkspace();
    const fake = new FakeClaudeRunner({ 'feature-delta': { newFeatures: [{ ...sampleFeatureSummary('F999', 'SEARCH', ['Api/Features/Search/SearchEndpoints.cs']), name: '검색' }], changedFeatureIds: ['F002', 'F777'], removedFeatures: [{ id: 'F001', disposition: 'MIGRATED', replacedBy: 'F003', reason: { text: 'r', status: 'CONFIRMED', evidence: [{ file: 'x' }] } }], unknowns: ['u'] } });
    const { ctx } = await makeTestContext(fake, { mode: 'INCREMENTAL' });
    const cs = changeSet([changed('Api/Features/Search/SearchEndpoints.cs', 'UNTRACKED')]);
    const impact = { affectedFeatures: ['F001'], candidateNewFeatureFiles: [cs.files[0].file], removalCandidates: [], affectedDocs: [], affectedDiagrams: [], needsArchitecture: true, needsData: false, needsApi: true, widenReason: [] };
    const delta = await runFeatureDelta(ctx, ws.features, cs, sampleClassification([cs.files[0].file]), impact);
    expect(nextFeatureIds(ws.features.features, 2)).toEqual(['F003', 'F004']);
    expect(delta.newFeatures[0].id).toBe('F003');
    expect(delta.changedFeatureIds).toEqual(['F001', 'F002']);
    expect(fake.calls[0].prompt).toContain('F003, F004');
    const applied = applyFeatureDelta(ws.features, delta);
    expect(applied.features.map((f) => `${f.id}:${f.status}`)).toEqual(['F001:REMOVED', 'F002:ACTIVE', 'F003:ACTIVE']);
    expect(applied.unknowns).toEqual(['u']);
  });
});
