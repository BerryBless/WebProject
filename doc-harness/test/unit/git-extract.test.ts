import { mkdirSync, writeFileSync } from 'node:fs';
import { mkdtemp } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { loadConfig } from '../../src/config.js';
import { git } from '../../src/git.js';
import { extractGitHistory, formatGitExtract } from '../../src/git-extract.js';

describe('git-extract', () => {
  it('키워드 히트 커밋만 diff를 가져오고 상한을 지키며 마커를 잡는다', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'dh-gx-'));
    await git(root, ['init', '-q', '-b', 'main']);
    await git(root, ['config', 'user.email', 't@example.com']);
    await git(root, ['config', 'user.name', 't']);
    mkdirSync(path.join(root, 'Api'), { recursive: true });
    writeFileSync(path.join(root, 'Api/A.cs'), 'public class A { }\n');
    await git(root, ['add', '-A']);
    await git(root, ['commit', '-q', '-m', '추가: 첫 파일']);
    writeFileSync(path.join(root, 'Api/A.cs'), 'public class A {\n  // TODO: retry on timeout\n  public void Fix() { }\n}\n' + 'x'.repeat(50_000));
    await git(root, ['add', '-A']);
    await git(root, ['commit', '-q', '-m', '버그수정: 타임아웃 레이스를 되돌림', '-m', '- 임시 우회']);
    const first = (await git(root, ['rev-list', '--max-parents=0', 'HEAD'])).trim();

    const cfg = loadConfig();
    const all = await extractGitHistory(root, cfg, null, false);
    expect(all.commits).toHaveLength(2);
    const hit = all.commits.find((c) => c.subject.startsWith('버그수정'))!;
    expect(hit.keywordHits).toEqual(expect.arrayContaining(['버그수정', '되돌', '임시']));
    expect(hit.files).toEqual(['Api/A.cs']);
    expect(Buffer.byteLength(hit.diffExcerpt)).toBeLessThanOrEqual(cfg.git.max_diff_bytes_per_commit + 40);
    expect(hit.diffExcerpt).toContain('diff 잘림');
    expect(all.commits.find((c) => c.subject.startsWith('추가'))!.diffExcerpt).toBe('');
    expect(all.markers).toEqual([{ file: 'Api/A.cs', line: 2, text: '// TODO: retry on timeout' }]);

    const since = await extractGitHistory(root, cfg, first, true);
    expect(since.commits).toHaveLength(1);
    expect(since.since).toBe(first);
    const text = formatGitExtract(all);
    expect(text).toContain('키워드 히트 1개');
    expect(text).toContain('```diff');
  });
});
