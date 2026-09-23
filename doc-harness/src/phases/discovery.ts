import { excerptJson, formatTruthLists, truthLists } from '../context.js';
import { buildPrompt } from '../prompts.js';
import type { Architecture, FeaturesFile, Inventory } from '../types.js';
import { callClaude, type PhaseContext } from './common.js';

export async function runDiscovery(ctx: PhaseContext, inventory: Inventory, architecture: Architecture): Promise<FeaturesFile> {
  const prompt = buildPrompt('03_feature_discovery', {
    maxFeatures: String(ctx.cfg.analysis.max_features),
    toolingDirs: ctx.cfg.project.tooling_dirs.join(', '),
    inventory: excerptJson(inventory as unknown as Record<string, unknown>, ['project', 'entryPoints', 'directories', 'scripts', 'containers'], 30_000),
    components: JSON.stringify(architecture.components.map((c) => ({ id: c.id, name: c.name, path: c.path, layer: c.layer, responsibilities: c.responsibilities })), null, 1),
    truthLists: formatTruthLists(truthLists(ctx.paths.projectRoot, ctx.cfg)),
  });
  const result = await callClaude<FeaturesFile>(ctx, { itemId: 'discovery', phase: 'discovery', schemaName: 'features', prompt, outFile: 'features.json' });
  // id 순서·형식을 하네스가 한 번 더 보정한다(LLM이 번호를 건너뛰는 경우).
  result.features.sort((a, b) => a.id.localeCompare(b.id));
  return result;
}
