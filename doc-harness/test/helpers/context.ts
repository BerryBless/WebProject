import { mkdtemp } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import type { ClaudeRunner } from '../../src/claude.js';
import { defaultHarnessDir, loadConfig, type HarnessConfig, type HarnessPaths } from '../../src/config.js';
import type { PhaseContext, Scope } from '../../src/phases/common.js';
import { Run } from '../../src/run.js';
import type { RunMode } from '../../src/types.js';

export const miniProjectRoot = path.join(defaultHarnessDir(), 'test', 'fixtures', 'mini-project');

/** 임시 workspace + mini-project를 대상으로 하는 PhaseContext. */
export async function makeTestContext(runner: ClaudeRunner, opts: { root?: string; mode?: RunMode; scope?: Scope; cfg?: HarnessConfig; sessionContext?: string; paths?: HarnessPaths } = {}): Promise<{ ctx: PhaseContext; paths: HarnessPaths; logs: string[] }> {
  const cfg = opts.cfg ?? loadConfig();
  const paths = opts.paths ?? (await testPaths(opts.root ?? miniProjectRoot));
  const run = await Run.create(paths, opts.mode ?? 'INITIAL', 'fp');
  const logs: string[] = [];
  const ctx: PhaseContext = { cfg, paths, run, runner, scope: opts.scope ?? { kind: 'ALL' }, log: (m) => logs.push(m), sessionContext: opts.sessionContext ?? '' };
  return { ctx, paths, logs };
}

export async function testPaths(root: string): Promise<HarnessPaths> {
  const base = await mkdtemp(path.join(os.tmpdir(), 'dh-ctx-'));
  const workspace = path.join(base, 'workspace');
  const harnessDir = defaultHarnessDir();
  return {
    harnessDir, projectRoot: root, workspace,
    current: path.join(workspace, 'current'), runs: path.join(workspace, 'runs'), inbox: path.join(workspace, 'inbox'),
    docsOut: path.join(base, 'docs', 'generated'),
    prompts: path.join(harnessDir, 'prompts'), schemas: path.join(harnessDir, 'schemas'), templates: path.join(harnessDir, 'templates'),
  };
}
