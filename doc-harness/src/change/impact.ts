import { depgraphDocsUnion } from './impact-util.js';
import { diagramsForFeatures, docsForFeatures, expandByDependencies, featuresForFiles } from '../depgraph.js';
import { ALWAYS_DOCS, CLASS_TO_DOCS } from '../docs-index.js';
import type { Architecture, ChangeClass, ChangeSet, Classification, Depgraph, FeatureSummary, Impact } from '../types.js';

const STRUCTURAL: ChangeClass[] = ['ARCHITECTURE_CHANGE', 'API_CHANGE', 'DATA_MODEL_CHANGE'];

/**
 * 결정적 영향 분석(스펙 §6.6). 직접 매핑 → 의존 1홉 → (구조 변경이면) 같은 컴포넌트의 기능으로 확대.
 * 확실하지 않으면 넓힌다: 기능에 매핑되지 않은 분석 대상 파일은 신규 기능 후보로, 삭제된 파일만 가진 기능은 삭제 후보로.
 */
export function analyzeImpact(changes: ChangeSet, classification: Classification, graph: Depgraph, features: FeatureSummary[], architecture: Architecture): Impact {
  const widenReason: string[] = [];
  const classByFile = new Map(classification.items.map((i) => [i.file, i]));
  const analysisFiles = changes.files.filter((f) => f.requiresAnalysis && f.changeType !== 'DELETED').map((f) => f.file);
  const allChanged = changes.files.map((f) => f.file);
  const renamedOld = changes.files.filter((f) => f.oldPath).map((f) => f.oldPath!);

  let affected = featuresForFiles(graph, [...allChanged, ...renamedOld]);
  for (const item of classification.items) for (const id of item.possibleFeatures) if (graph.features[id] && !affected.includes(id)) affected.push(id);
  const direct = new Set(affected);
  // 분석 대상 변경이 전부 TRIVIAL(주석·포맷·문서)이면 직접 매핑된 기능만 재검증한다. 의존 확장은 동작이 바뀔 수 있을 때만(2026-09-25 실측: 주석 한 줄에 기능 5개 재분석).
  const trivialOnly = analysisFiles.length > 0 && analysisFiles.every((f) => (classByFile.get(f)?.significance ?? 'MINOR') === 'TRIVIAL');
  if (!trivialOnly) {
    affected = expandByDependencies(graph, affected);
    if (affected.length > direct.size) widenReason.push(`의존 1홉 확장: ${affected.filter((a) => !direct.has(a)).join(', ')}`);
  } else if (affected.length) {
    widenReason.push('변경이 전부 TRIVIAL이라 의존 확장 없이 직접 기능만 재검증');
  }

  const allClasses = new Set(classification.items.flatMap((i) => i.classifications));
  const structural = trivialOnly ? [] : STRUCTURAL.filter((c) => allClasses.has(c));
  if (structural.length) {
    const changedComponents = architecture.components.filter((c) => allChanged.some((f) => f === c.path || f.startsWith(c.path.replace(/\/[^/]*$/, '') + '/') || c.evidence.some((e) => e.file === f)));
    for (const comp of changedComponents) {
      const dir = comp.path.includes('.') ? comp.path.replace(/\/[^/]*$/, '') : comp.path;
      for (const [id, node] of Object.entries(graph.features)) {
        if (!affected.includes(id) && node.files.some((f) => f.startsWith(dir + '/') || f === comp.path)) {
          affected.push(id);
          widenReason.push(`${structural.join('/')}로 컴포넌트 ${comp.name}의 기능 ${id} 확대`);
        }
      }
    }
  }
  affected.sort();

  const candidateNewFeatureFiles = analysisFiles.filter((f) => !(graph.files[f]?.features.length) && !classByFile.get(f)?.possibleFeatures.length);
  const newFeatureFlagged = classification.items.filter((i) => i.classifications.includes('NEW_FEATURE')).map((i) => i.file);
  for (const f of newFeatureFlagged) if (!candidateNewFeatureFiles.includes(f)) candidateNewFeatureFiles.push(f);
  if (candidateNewFeatureFiles.length) widenReason.push(`기능에 매핑되지 않은 변경 파일 ${candidateNewFeatureFiles.length}개 → 신규 기능 탐지 대상`);

  const deleted = new Set(changes.files.filter((f) => f.changeType === 'DELETED').map((f) => f.file));
  const removalCandidates = features.filter((f) => f.status !== 'REMOVED' && f.relatedFiles.length && f.relatedFiles.every((rf) => deleted.has(rf))).map((f) => f.id);

  const docs = new Set(docsForFeatures(graph, affected));
  for (const c of allClasses) for (const d of CLASS_TO_DOCS[c] ?? []) docs.add(d);
  for (const d of depgraphDocsUnion(graph, allChanged)) docs.add(d);
  for (const d of ALWAYS_DOCS) docs.add(d);

  return {
    affectedFeatures: affected,
    candidateNewFeatureFiles,
    removalCandidates,
    affectedDocs: [...docs].sort(),
    affectedDiagrams: diagramsForFeatures(graph, affected, allChanged),
    needsArchitecture: allClasses.has('ARCHITECTURE_CHANGE') || allClasses.has('DEPLOYMENT_CHANGE') || candidateNewFeatureFiles.length > 0,
    needsData: allClasses.has('DATA_MODEL_CHANGE'),
    needsApi: allClasses.has('API_CHANGE') || allClasses.has('NEW_FEATURE'),
    widenReason,
  };
}
