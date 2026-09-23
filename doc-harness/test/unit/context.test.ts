import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { defaultHarnessDir, loadConfig } from '../../src/config.js';
import { excerptJson, fileTree, formatTruthLists, manifestFiles, pathList, truthLists } from '../../src/context.js';
import { listSourceFiles } from '../../src/fsx.js';

const root = path.join(defaultHarnessDir(), 'test', 'fixtures', 'mini-project');
const cfg = loadConfig();

describe('context', () => {
  it('fileTree는 디렉터리별로 묶고 제외 규칙을 지킨다', () => {
    const tree = fileTree(root, cfg);
    expect(tree).toContain('Api/Features/Posts/');
    expect(tree).toContain('  PostEndpoints.cs');
    expect(tree).not.toContain('node_modules');
  });

  it('truthLists는 엔드포인트·엔티티·페이지·SPA 라우트·설정 키를 잡고 값은 읽지 않는다', () => {
    const t = truthLists(root, cfg);
    expect(t.endpoints).toEqual(expect.arrayContaining([
      { method: 'GET', path: '/api/posts', file: 'Api/Features/Posts/PostEndpoints.cs' },
      { method: 'POST', path: '/api/auth/login', file: 'Api/Features/Auth/AuthEndpoints.cs' },
    ]));
    expect(t.entities.map((e) => e.name)).toEqual(['Post', 'Tag']);
    expect(t.pages).toEqual([{ route: '/', file: 'Api/Pages/Index.cshtml' }]);
    expect(t.spaRoutes.map((r) => r.route)).toEqual(['/editor', '/editor/:id', '/login']);
    expect(t.configKeys.map((c) => c.key)).toEqual(expect.arrayContaining(['Blog', 'Blog:AllowedIps', 'ConnectionStrings:Default']));
    const text = formatTruthLists(t);
    expect(text).not.toContain('changeme');
    expect(text).not.toContain('203.0.113.4');
  });

  it('manifestFiles와 pathList는 매니페스트만 고르고 비밀 파일을 뺀다', () => {
    const files = listSourceFiles(root, cfg);
    expect(manifestFiles(files)).toEqual(['Mini.Api.csproj', 'Web/package.json']);
    expect(pathList(files, cfg)).not.toContain('appsettings.json');
    expect(pathList(files, cfg)).toContain('- Api/Program.cs');
  });

  it('excerptJson은 지정 키만 뽑고 길이를 자른다', () => {
    expect(excerptJson({ a: 1, b: 2 }, ['a'])).toBe('{\n "a": 1\n}');
    expect(excerptJson({ a: 'x'.repeat(100) }, ['a'], 20)).toContain('…(잘림)');
  });
});
