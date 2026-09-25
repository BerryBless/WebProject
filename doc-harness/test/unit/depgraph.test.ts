import { describe, expect, it } from 'vitest';
import { buildDepgraph, diagramsForFeatures, docsForFeatures, expandByDependencies, featuresForFiles } from '../../src/depgraph.js';
import type { CurrentWorkspace } from '../../src/types.js';
import { sampleApi, sampleArchitecture, sampleData, sampleFailures, sampleFeatureAnalysis, sampleFeatures, sampleInventory, sampleOperations } from '../helpers/samples.js';

export function sampleWorkspace(): CurrentWorkspace {
  const features = sampleFeatures();
  features.features[1].dependencies = ['F001'];
  return {
    inventory: sampleInventory(), architecture: sampleArchitecture(), features,
    featureAnalyses: new Map(features.features.map((f) => [f.id, sampleFeatureAnalysis(f)])),
    data: sampleData(), api: sampleApi(), failures: sampleFailures(), operations: sampleOperations(),
  };
}

describe('depgraph', () => {
  it('evidence에서 파일↔기능↔API/엔티티↔다이어그램↔문서를 유도한다', () => {
    const g = buildDepgraph(sampleWorkspace());
    expect(g.files['Api/Features/Posts/PostEndpoints.cs'].features).toEqual(['F001']);
    expect(g.files['Api/Features/Posts/PostEndpoints.cs'].apis).toEqual(['GET /api/posts', 'POST /api/posts']);
    expect(g.files['Api/Features/Posts/PostEndpoints.cs'].diagrams).toContain('F001_SEQUENCE');
    expect(g.files['Api/Features/Posts/PostEndpoints.cs'].documents).toEqual(expect.arrayContaining(['features/F001_POST_LIST.md', '08_API.md', '09_FEATURES.md', '02_ARCHITECTURE.md']));
    expect(g.files['Api/Infrastructure/Data/AppDbContext.cs'].entities).toEqual(['Post', 'Tag']);
    expect(g.files['Api/Infrastructure/Data/AppDbContext.cs'].documents).toContain('07_DATA_MODEL.md');
    expect(g.features.F001.entities).toEqual(['Post']);
    expect(g.features.F002.dependencies).toEqual(['F001']);
    expect(g.documents['features/F001_POST_LIST.md'].features).toEqual(['F001']);
    expect(g.documents['08_API.md'].inputs).toEqual(['api']);
  });

  it('조회 도우미: 파일→기능, 의존 확장(양방향), 문서·다이어그램', () => {
    const g = buildDepgraph(sampleWorkspace());
    expect(featuresForFiles(g, ['Api/Features/Auth/AuthEndpoints.cs'])).toEqual(['F002']);
    expect(expandByDependencies(g, ['F001'])).toEqual(['F001', 'F002']);
    expect(expandByDependencies(g, ['F002'])).toEqual(['F001', 'F002']);
    expect(docsForFeatures(g, ['F002'])).toEqual(expect.arrayContaining(['features/F002_ADMIN_LOGIN.md', '00_EXECUTIVE_SUMMARY.md', '20_CHANGELOG.md']));
    expect(diagramsForFeatures(g, ['F002'], [])).toEqual(['F002_SEQUENCE']);
  });
});
