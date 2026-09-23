import { formatChanges, featureIndexJson } from './classify.js';
import { buildPrompt } from '../prompts.js';
import { callClaude, type PhaseContext } from '../phases/common.js';
import type { ChangeSet, Classification, FeatureDelta, FeatureSummary, FeaturesFile, Impact } from '../types.js';

export function nextFeatureIds(features: FeatureSummary[], count: number): string[] {
  const max = features.reduce((m, f) => Math.max(m, Number(f.id.replace('F', '')) || 0), 0);
  return Array.from({ length: count }, (_, i) => `F${String(max + 1 + i).padStart(3, '0')}`);
}

export async function runFeatureDelta(ctx: PhaseContext, features: FeaturesFile, changes: ChangeSet, classification: Classification, impact: Impact): Promise<FeatureDelta> {
  const reserved = nextFeatureIds(features.features, 6);
  const prompt = buildPrompt('I04_feature_delta', {
    features: featureIndexJson(features.features),
    reservedIds: reserved.join(', '),
    classification: [classification.summary, ...classification.items.map((i) => `- ${i.file}: ${i.classifications.join('/')} [${i.significance}]${i.possibleFeatures.length ? ` → ${i.possibleFeatures.join(', ')}` : ''}`)].join('\n'),
    impact: JSON.stringify({ affectedFeatures: impact.affectedFeatures, candidateNewFeatureFiles: impact.candidateNewFeatureFiles, removalCandidates: impact.removalCandidates, widenReason: impact.widenReason }, null, 1),
    changes: formatChanges(changes, { maxFiles: 40, maxLines: 30 }),
  });
  const out = await callClaude<FeatureDelta>(ctx, { itemId: 'feature-delta', phase: 'feature_delta', schemaName: 'feature_delta', prompt, outFile: 'feature_delta.json', extraHash: changes.fingerprint });
  // id는 하네스가 확정한다(LLM이 예약 id를 안 지켰거나 겹치면 재부여).
  const existing = new Set(features.features.map((f) => f.id));
  const fresh = nextFeatureIds(features.features, out.newFeatures.length);
  out.newFeatures = out.newFeatures.filter((nf) => !existing.has(nf.id) || true).map((nf, i) => ({ ...nf, id: fresh[i], analysisStatus: 'PENDING', status: 'ACTIVE' }));
  out.changedFeatureIds = [...new Set([...out.changedFeatureIds, ...impact.affectedFeatures])].filter((id) => existing.has(id)).sort();
  out.removedFeatures = out.removedFeatures.filter((r) => existing.has(r.id));
  return out;
}

/** 델타를 features.json에 적용한다: 신규 추가, 삭제 처분 반영(REMOVED는 status만 바꾸고 보존). */
export function applyFeatureDelta(features: FeaturesFile, delta: FeatureDelta): FeaturesFile {
  const list = features.features.map((f) => ({ ...f }));
  for (const r of delta.removedFeatures) {
    const f = list.find((x) => x.id === r.id);
    if (!f) continue;
    if (r.disposition === 'REMOVED' || r.disposition === 'MIGRATED') f.status = 'REMOVED';
    else if (r.disposition === 'DEPRECATED') f.status = 'DEPRECATED';
  }
  for (const nf of delta.newFeatures) if (!list.some((x) => x.id === nf.id)) list.push(nf);
  list.sort((a, b) => a.id.localeCompare(b.id));
  return { features: list, excludedCandidates: features.excludedCandidates, unknowns: [...new Set([...features.unknowns, ...delta.unknowns])] };
}
