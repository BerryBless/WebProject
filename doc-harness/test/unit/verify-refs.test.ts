import { describe, expect, it } from 'vitest';
import { loadConfig } from '../../src/config.js';
import { truthLists } from '../../src/context.js';
import { renderApiDoc, renderDataDoc, renderFeatureDoc } from '../../src/render/templates.js';
import { checkConsistency } from '../../src/verify/consistency.js';
import { checkReferences } from '../../src/verify/references.js';
import { miniProjectRoot } from '../helpers/context.js';
import { sampleFeatureAnalysis } from '../helpers/samples.js';
import { sampleWorkspace } from './depgraph.test.js';

describe('checkReferences', () => {
  it('없는 경로·기능 id·깨진 링크를 잡고 정상 참조는 통과시킨다', () => {
    const ws = sampleWorkspace();
    const { name, md } = renderFeatureDoc(sampleFeatureAnalysis());
    const docs = new Map<string, string>([
      [name, md],
      ['09_FEATURES.md', '# x\n[F001](features/F001_POST_LIST.md) [없음](features/F009_NOPE.md)'],
      ['08_API.md', '# api\n`Api/Nope.cs` 와 F999 를 언급'],
      ['07_DATA_MODEL.md', '# d'], ['11_FAILURE_HISTORY.md', '# f'],
    ]);
    const issues = checkReferences(docs, miniProjectRoot, ws);
    expect(issues.filter((i) => i.document === name)).toEqual([]);
    expect(issues.map((i) => [i.document, i.type])).toEqual(expect.arrayContaining([['09_FEATURES.md', 'BROKEN_LINK'], ['08_API.md', 'HALLUCINATED_PATH'], ['08_API.md', 'UNKNOWN_FEATURE_ID']]));
    expect(issues).toHaveLength(3);
  });
});

describe('checkConsistency', () => {
  it('08_API·07_DATA_MODEL·02_ARCHITECTURE 집합 대조와 코드 정답 목록 대조', () => {
    const ws = sampleWorkspace();
    const cfg = loadConfig();
    const docs = new Map<string, string>([
      ['08_API.md', renderApiDoc(ws)], ['07_DATA_MODEL.md', renderDataDoc(ws)],
      ['02_ARCHITECTURE.md', '# arch\nPostEndpoints, AuthEndpoints 그리고 AppDbContext'],
      ['features/F001_POST_LIST.md', 'GET /api/posts 와 POST /api/posts'], ['features/F002_ADMIN_LOGIN.md', 'POST /api/auth/login'],
    ]);
    expect(checkConsistency(docs, ws, truthLists(miniProjectRoot, cfg))).toEqual([]);

    ws.api.endpoints.pop(); // api.json에서 로그인 제거 → 코드 정답 목록과 어긋남 + 문서 표에 EXTRA
    ws.data.entities.pop(); // Tag 제거 → 코드 DbSet과 어긋남
    docs.set('02_ARCHITECTURE.md', '# arch\nPostEndpoints만');
    docs.set('features/F001_POST_LIST.md', 'GET /api/posts');
    const issues = checkConsistency(docs, ws, truthLists(miniProjectRoot, cfg));
    const types = issues.map((i) => i.type);
    expect(types).toEqual(expect.arrayContaining(['EXTRA_ENDPOINT_IN_DOC', 'MISSING_ENDPOINT', 'MISSING_ENTITY', 'MISSING_COMPONENT_IN_DOC', 'ENDPOINT_NOT_IN_FEATURE_DOCS']));
    expect(issues.find((i) => i.type === 'MISSING_ENDPOINT')?.description).toContain('POST /api/auth/login');
    expect(issues.find((i) => i.type === 'MISSING_ENTITY')?.description).toContain('Tag');
  });
});
