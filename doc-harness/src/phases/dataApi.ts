import { readFileSync } from 'node:fs';
import path from 'node:path';
import { formatTruthLists, pathList, truthLists } from '../context.js';
import { listSourceFiles } from '../fsx.js';
import { buildPrompt } from '../prompts.js';
import type { ApiModel, DataModel, FeatureSummary } from '../types.js';
import { callClaude, type PhaseContext } from './common.js';
import { fixTail, type FixHint } from './feature.js';

const DATA_HINT_RE = /(DbContext|DbSet<|\[Table\(|Migration|ModelSnapshot|HasIndex|HasKey|npgsql|CREATE TABLE)/i;
const API_HINT_RE = /(\.Map(Get|Post|Put|Delete|Patch|Group)\(|@page|MapRazorPages|UseStaticFiles|MapFallback|\.MapHealthChecks)/;

function filesMatching(root: string, files: string[], re: RegExp, extraExt: string[] = []): string[] {
  return files.filter((f) => {
    if (!(f.endsWith('.cs') || f.endsWith('.cshtml') || f.endsWith('.sql') || extraExt.some((e) => f.endsWith(e)))) return false;
    try { return re.test(readFileSync(path.join(root, f), 'utf8')); } catch { return false; }
  });
}

function featureIndex(features: FeatureSummary[]): string {
  return JSON.stringify(features.map((f) => ({ id: f.id, name: f.name, entryPoints: f.entryPoints, status: f.status })), null, 1);
}

export async function runData(ctx: PhaseContext, features: FeatureSummary[], fix?: FixHint): Promise<DataModel> {
  const files = listSourceFiles(ctx.paths.projectRoot, ctx.cfg);
  const truth = truthLists(ctx.paths.projectRoot, ctx.cfg, files);
  const { tail, suffix } = fixTail(fix);
  const prompt = buildPrompt('05_data', {
    dataFiles: pathList(filesMatching(ctx.paths.projectRoot, files, DATA_HINT_RE), ctx.cfg) || '(없음)',
    entities: truth.entities.map((e) => `- ${e.name}  (${e.file})`).join('\n') || '(없음)',
    features: featureIndex(features),
  }, { tail });
  return callClaude<DataModel>(ctx, { itemId: `data${suffix}`, phase: 'data', schemaName: 'data', prompt, outFile: 'data.json' });
}

export async function runApi(ctx: PhaseContext, features: FeatureSummary[], fix?: FixHint): Promise<ApiModel> {
  const files = listSourceFiles(ctx.paths.projectRoot, ctx.cfg);
  const truth = truthLists(ctx.paths.projectRoot, ctx.cfg, files);
  const { tail, suffix } = fixTail(fix);
  const prompt = buildPrompt('05_api', {
    apiFiles: pathList(filesMatching(ctx.paths.projectRoot, files, API_HINT_RE), ctx.cfg) || '(없음)',
    truthLists: formatTruthLists(truth),
    features: featureIndex(features),
  }, { tail });
  return callClaude<ApiModel>(ctx, { itemId: `api${suffix}`, phase: 'api', schemaName: 'api', prompt, outFile: 'api.json' });
}
