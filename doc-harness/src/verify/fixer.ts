import { featureDocName, NARRATIVE_DOCS } from '../docs-index.js';
import type { PhaseContext, Scope } from '../phases/common.js';
import { runApi, runData } from '../phases/dataApi.js';
import type { Journal } from '../phases/failures.js';
import { analyzeFeature } from '../phases/feature.js';
import { updateDocs } from '../render/documents.js';
import type { Baseline, ChangeSet, CurrentWorkspace, ManualOverride } from '../types.js';

export interface FixerDeps {
  ws: CurrentWorkspace;
  journal: Journal;
  scope: Scope;
  prevDocs: Map<string, string>;
  baseline: Baseline | null;
  changes: ChangeSet | null;
}

/**
 * 검증 이슈를 문서 종류별로 되돌려 고친다. 프로덕션 코드는 건드리지 않는다.
 * - 서술 문서: 이슈를 주입해 LLM이 해당 문서를 다시 쓴다.
 * - 기능 문서: 기능을 이슈와 함께 재분석하고 다시 렌더한다.
 * - 08_API / 07_DATA_MODEL: api/data 분석을 이슈와 함께 다시 돌린다.
 * - 그 밖의 템플릿 문서: 원천 JSON이 바뀌었을 수 있으므로 다시 렌더만 한다.
 */
export function makeFixer(ctx: PhaseContext, deps: FixerDeps) {
  return async (issues: Map<string, string[]>, iteration: number): Promise<{ docs: Map<string, string>; overridden: ManualOverride[] }> => {
    const { ws } = deps;
    const only = new Set<string>();
    for (const [doc, list] of issues) {
      only.add(doc);
      const fid = /^features\/(F\d{3})_/.exec(doc)?.[1];
      const summary = fid ? ws.features.features.find((f) => f.id === fid) : undefined;
      if (fid && summary) {
        ctx.log(`수정 ${iteration}: ${fid} 재분석 (${list.length}건)`);
        const fa = await analyzeFeature(ctx, summary, ws.architecture, ws.featureAnalyses.get(fid) ?? null, deps.changes, { issues: list, attempt: iteration }, ws.features.features);
        ws.featureAnalyses.set(fid, fa);
        only.add(featureDocName(fid, fa.feature.slug));
        only.add('09_FEATURES.md');
      } else if (doc === '08_API.md') {
        ctx.log(`수정 ${iteration}: API 재분석 (${list.length}건)`);
        ws.api = await runApi(ctx, ws.features.features, { issues: list, attempt: iteration });
      } else if (doc === '07_DATA_MODEL.md') {
        ctx.log(`수정 ${iteration}: 데이터 모델 재분석 (${list.length}건)`);
        ws.data = await runData(ctx, ws.features.features, { issues: list, attempt: iteration });
      } else if (doc === '09_FEATURES.md' || doc === 'README.md') {
        // 기능 색인의 이슈는 정확히 한 기능만 가리킬 때만 그 기능을 재분석한다. 여러 id를 나열한 지적을 전부에 부채질하면
        // 기능 10여 개가 같은 이슈로 재분석된다(2026-09-25 실측 약 $16). 색인 자체는 템플릿이라 재렌더로 충분하다.
        for (const issue of list) {
          const ids = [...new Set(issue.match(/\bF\d{3}\b/g) ?? [])];
          if (ids.length !== 1) continue;
          const s = ws.features.features.find((f) => f.id === ids[0]);
          if (s) { const name = featureDocName(s.id, s.slug); only.add(name); issues.set(name, [...(issues.get(name) ?? []), issue]); }
        }
      }
    }
    const narrativeIssues = new Map<string, string[]>();
    for (const [doc, list] of issues) if ((NARRATIVE_DOCS as readonly string[]).includes(doc) || doc.startsWith('features/')) narrativeIssues.set(doc, list);
    const r = await updateDocs(ctx, { ws, journal: deps.journal, scope: deps.scope, prevDocs: deps.prevDocs, baseline: deps.baseline, changes: deps.changes, prevAnalyses: new Map(), issues: narrativeIssues, onlyDocs: only, fixAttempt: iteration });
    const overridden = r.manualEdits.filter((m) => !m.kept).map((m) => ({ document: m.document, section: m.section, reason: (issues.get(m.document) ?? []).find((i) => i.includes(`section=${m.section}`)) ?? '검증 이슈' }));
    return { docs: r.written, overridden };
  };
}
