import { fileTree, manifestFiles, pathList } from '../context.js';
import { listSourceFiles } from '../fsx.js';
import { buildPrompt } from '../prompts.js';
import type { Inventory } from '../types.js';
import { callClaude, type PhaseContext } from './common.js';

export async function runInventory(ctx: PhaseContext): Promise<Inventory> {
  const files = listSourceFiles(ctx.paths.projectRoot, ctx.cfg);
  const prompt = buildPrompt('01_inventory', {
    fileTree: fileTree(ctx.paths.projectRoot, ctx.cfg, files),
    manifests: pathList(manifestFiles(files), ctx.cfg) || '(없음)',
    toolingDirs: ctx.cfg.project.tooling_dirs.join(', '),
  });
  return callClaude<Inventory>(ctx, { itemId: 'inventory', phase: 'inventory', schemaName: 'inventory', prompt, outFile: 'inventory.json' });
}
