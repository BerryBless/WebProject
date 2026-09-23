import { mkdirSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { mkdtemp } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { emptyBaseline, fingerprint } from '../../src/baseline.js';
import { detectChanges, extractSymbols, hashSourceFiles } from '../../src/change/detect.js';
import { loadConfig, type HarnessPaths } from '../../src/config.js';
import { git, headCommit } from '../../src/git.js';

async function repo(): Promise<{ root: string; paths: HarnessPaths }> {
  const root = await mkdtemp(path.join(os.tmpdir(), 'dh-git-'));
  await git(root, ['init', '-q', '-b', 'main']);
  await git(root, ['config', 'user.email', 'test@example.com']);
  await git(root, ['config', 'user.name', 'test']);
  const write = (rel: string, text: string) => { mkdirSync(path.dirname(path.join(root, rel)), { recursive: true }); writeFileSync(path.join(root, rel), text); };
  write('Api/Program.cs', 'public class Program { }\n');
  write('Api/Features/PostEndpoints.cs', 'public static class PostEndpoints {\n  public static void Map(WebApplication app) { app.MapGet("/api/posts", () => 1); }\n}\n');
  write('Api/Old.cs', 'public class Old { }\n');
  write('README.md', '# readme\n');
  write('Api.Tests/PostTests.cs', 'public class PostTests { }\n');
  await git(root, ['add', '-A']);
  await git(root, ['commit', '-q', '-m', 'init']);
  const paths: HarnessPaths = { harnessDir: root, projectRoot: root, workspace: path.join(root, 'ws'), current: '', runs: '', inbox: '', docsOut: '', prompts: '', schemas: '', templates: '' };
  return { root, paths };
}

describe('detectChanges', () => {
  it('baseline이 없으면 전부 ADDED이고 fingerprint가 파일 해시에서 나온다', async () => {
    const { paths } = await repo();
    const cfg = loadConfig();
    const cs = await detectChanges(paths, cfg, null);
    expect(cs.files.every((f) => f.changeType === 'ADDED')).toBe(true);
    expect(cs.files.map((f) => f.file)).toContain('Api/Program.cs');
    expect(cs.fingerprint).toBe(fingerprint(hashSourceFiles(paths.projectRoot, cfg)));
    expect(cs.headCommit).toHaveLength(40);
  });

  it('수정·삭제·untracked·rename·문서 전용 변경을 분류한다', async () => {
    const { root, paths } = await repo();
    const cfg = loadConfig();
    const base = emptyBaseline(await headCommit(root), '');
    for (const f of hashSourceFiles(root, cfg)) base.files[f.path] = { hash: f.hash, features: [], documents: [], diagrams: [] };

    writeFileSync(path.join(root, 'Api/Features/PostEndpoints.cs'), 'public static class PostEndpoints {\n  public static void Map(WebApplication app) { app.MapGet("/api/posts", () => 1); app.MapPost("/api/posts", () => 2); }\n}\n');
    rmSync(path.join(root, 'Api/Program.cs'));
    mkdirSync(path.join(root, 'Api/Legacy'), { recursive: true });
    renameSync(path.join(root, 'Api/Old.cs'), path.join(root, 'Api/Legacy/Old.cs'));
    writeFileSync(path.join(root, 'Api/New.cs'), 'public class NewThing { }\n');
    writeFileSync(path.join(root, 'README.md'), '# readme changed\n');
    writeFileSync(path.join(root, 'Api.Tests/PostTests.cs'), 'public class PostTests { public void X() { } }\n');

    const cs = await detectChanges(paths, cfg, base);
    const by = Object.fromEntries(cs.files.map((f) => [f.file, f]));
    expect(by['Api/Features/PostEndpoints.cs'].changeType).toBe('MODIFIED');
    expect(by['Api/Features/PostEndpoints.cs'].addedLines.join('\n')).toContain('MapPost');
    expect(by['Api/Features/PostEndpoints.cs'].relatedSymbols).toContain('/api/posts');
    expect(by['Api/Program.cs'].changeType).toBe('DELETED');
    expect(by['Api/Legacy/Old.cs'].changeType).toBe('RENAMED');
    expect(by['Api/Legacy/Old.cs'].oldPath).toBe('Api/Old.cs');
    expect(by['Api/Old.cs']).toBeUndefined();
    expect(by['Api/New.cs'].changeType).toBe('UNTRACKED');
    expect(by['Api/New.cs'].untracked).toBe(true);
    expect(by['Api/New.cs'].relatedSymbols).toEqual(['NewThing']);
    expect(by['README.md'].requiresAnalysis).toBe(false);
    expect(by['Api.Tests/PostTests.cs'].requiresAnalysis).toBe(false);
    expect(by['Api/New.cs'].requiresAnalysis).toBe(true);
    expect(cs.baselineCommit).toBe(base.baselineCommit);
  });

  it('baseline 커밋이 저장소에 없으면 baselineCommit은 null이지만 해시 대조는 계속된다', async () => {
    const { root, paths } = await repo();
    const cfg = loadConfig();
    const base = emptyBaseline('0000000000000000000000000000000000000000', '');
    for (const f of hashSourceFiles(root, cfg)) base.files[f.path] = { hash: f.hash, features: [], documents: [], diagrams: [] };
    writeFileSync(path.join(root, 'Api/Program.cs'), 'public class Program { static void Main() { } }\n');
    const cs = await detectChanges(paths, cfg, base);
    expect(cs.baselineCommit).toBeNull();
    expect(cs.files.map((f) => f.file)).toEqual(['Api/Program.cs']);
    expect(cs.files[0].changeType).toBe('MODIFIED');
  });

  it('extractSymbols는 C#·TS 선언과 엔드포인트 경로를 잡는다', () => {
    expect(extractSymbols(['public sealed class Foo : Bar', 'export const useX = () => 1', 'app.MapDelete("/api/x/{id}", ...)', '    public async Task<int> DoWork(int a)'])).toEqual(['/api/x/{id}', 'DoWork', 'Foo', 'useX']);
  });
});
