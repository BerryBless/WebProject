// 워크스페이스 JSON에서 결정적으로 만드는 문서들. LLM을 부르지 않으므로 환각이 없다.
import { adrDocName, featureDocName } from '../docs-index.js';
import type { Journal } from '../phases/failures.js';
import type { Claim, CurrentWorkspace, Evidence, FeatureAnalysis, FeatureSummary } from '../types.js';
import { assembleDoc, parseSections } from './sections.js';
import { renderDiagramSection } from './diagrams.js';

const esc = (s: string) => s.replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
const code = (s: string) => `\`${s}\``;
const evid = (e: Evidence[]) => e.map((x) => `${code(x.file)}${x.symbol ? ` ${x.symbol}` : ''}${x.lines ? ` (${x.lines})` : ''}`).join(', ') || '-';
const claimRows = (cs: Claim[]) => cs.length ? ['| 내용 | 상태 | 근거 |', '|---|---|---|', ...cs.map((c) => `| ${esc(c.text)} | ${c.status} | ${esc(evid(c.evidence))} |`)].join('\n') : '_(없음)_';
const related = (docs: string[]) => docs.map((d) => `- [${d.replace(/\.md$/, '')}](${d.split('/').length > 1 ? d : d})`).join('\n');

function relLink(from: string, to: string): string {
  const up = from.includes('/') ? '../' : '';
  return `${up}${to}`;
}

export interface RenderExtras {
  journal: Journal;
  /** 기존 문서(증분 append용). */
  prevDocs: Map<string, string>;
  /** 저장소 파일 트리 텍스트(03_DIRECTORY_STRUCTURE). */
  fileTree: string;
  today: string;
}

export function featureTitle(f: FeatureSummary): string {
  return `${f.id} ${f.name}`;
}

/** 기능 문서: 요약 → 처리 흐름 → Diagram(각각 섹션) → 데이터 → 실패 지점 → 관련 코드 → 변경 이력 → 미확인 → 관련 문서. */
export function renderFeatureDoc(fa: FeatureAnalysis): { name: string; md: string } {
  const f = fa.feature;
  const name = featureDocName(f.id, f.slug);
  const sections: { id: string; body: string }[] = [];
  sections.push({ id: 'summary', body: [
    '## 한 줄 요약', '', fa.summary.trim(), '',
    '| 항목 | 값 |', '|---|---|', `| 중요도 | ${f.importance} |`, `| 상태 | ${f.status} |`, `| 진입점 | ${f.entryPoints.map(code).join(', ') || '-'} |`, `| 의존 기능 | ${f.dependencies.map((d) => `[${d}](${relLink(name, '09_FEATURES.md')}#${d.toLowerCase()})`).join(', ') || '-'} |`,
    '', '### 진입점 근거', '', claimRows(fa.entryPoints),
  ].join('\n') });
  sections.push({ id: 'flow', body: [
    '## 처리 흐름', '', '| 단계 | 컴포넌트 | 코드 | 설명 |', '|---|---|---|---|',
    ...fa.executionFlow.map((s) => `| ${s.step} | ${esc(s.component)} | ${code(s.file)}${s.symbol ? ` ${esc(s.symbol)}` : ''} | ${esc(s.description)} |`),
  ].join('\n') });
  for (const d of fa.diagrams) sections.push(renderDiagramSection(d));
  sections.push({ id: 'data', body: [
    '## 데이터', '', '### 데이터 흐름', '', claimRows(fa.dataFlow), '',
    '### DB 접근', '', fa.databaseAccess.length ? ['| 엔티티 | 작업 | 코드 |', '|---|---|---|', ...fa.databaseAccess.map((d) => `| ${esc(d.entity)} | ${esc(d.operation)} | ${code(d.file)}${d.symbol ? ` ${esc(d.symbol)}` : ''} |`)].join('\n') : '_(없음)_', '',
    '### 상태 전이', '', fa.stateTransitions.length ? ['| 이전 | 다음 | 트리거 | 근거 |', '|---|---|---|---|', ...fa.stateTransitions.map((t) => `| ${esc(t.from)} | ${esc(t.to)} | ${esc(t.trigger)} | ${esc(evid(t.evidence))} |`)].join('\n') : '_(상태 없음)_', '',
    '### 외부 의존', '', claimRows(fa.externalDependencies),
  ].join('\n') });
  sections.push({ id: 'failures', body: [
    '## 실패 지점', '', fa.failurePoints.length ? ['| 위치 | 조건 | 처리 | 상태 | 근거 |', '|---|---|---|---|---|', ...fa.failurePoints.map((p) => `| ${esc(p.where)} | ${esc(p.condition)} | ${esc(p.handling)} | ${p.status} | ${esc(evid(p.evidence))} |`)].join('\n') : '_(확인된 실패 지점 없음)_', '',
    '### 엣지 케이스', '', claimRows(fa.edgeCases), '', '### 로깅', '', claimRows(fa.logging),
  ].join('\n') });
  sections.push({ id: 'code', body: [
    '## 관련 코드', '', '| 파일 | 심볼 | 역할 |', '|---|---|---|', ...fa.relatedCode.map((c) => `| ${code(c.file)} | ${esc(c.symbol ?? '-')} | ${esc(c.role)} |`), '',
    '근거: ' + evid(fa.evidence),
  ].join('\n') });
  const history = fa.history.filter((h) => h.significance !== 'TRIVIAL');
  if (history.length) {
    sections.push({ id: 'history', body: ['## 변경 이력', '', ...history.map((h) => [
      `### ${h.date} — ${h.title}`, '', `분류: ${h.classification.join(' / ')} (${h.significance})`, '', `**기존:** ${h.before}`, '', `**현재:** ${h.after}`, '',
      `**변경 이유:** [${h.reason.status}] ${h.reason.text}${h.reason.evidence.length ? ` — ${evid(h.reason.evidence)}` : ''}`, '',
      h.impact.length ? `**영향:**\n${h.impact.map((i) => `- ${i}`).join('\n')}` : '', h.commits.length ? `관련 커밋: ${h.commits.map(code).join(', ')}` : '',
    ].filter((x) => x !== '').join('\n'))].join('\n\n') });
  }
  if (fa.unknowns.length) sections.push({ id: 'unknowns', body: ['## 확인하지 못한 것', '', ...fa.unknowns.map((u) => `- ${u}`)].join('\n') });
  sections.push({ id: 'related', body: ['## 관련 문서', '', related([relLink(name, '09_FEATURES.md'), relLink(name, '08_API.md'), relLink(name, '07_DATA_MODEL.md'), relLink(name, '11_FAILURE_HISTORY.md')])].join('\n') });
  return { name, md: assembleDoc(`${featureTitle(f)}`, sections) };
}

/** 기능 간 의존: 노드 25개·간선 40개 이하면 flowchart(라벨은 따옴표로 감싸 괄호·특수문자 안전), 넘으면 표로 낸다(복잡도 상한 D14). */
function renderDependencies(active: FeatureSummary[]): string {
  const withDeps = active.filter((f) => f.dependencies.length);
  if (!withDeps.length) return '_(기능 간 의존 없음)_';
  const nodes = new Set<string>();
  for (const f of withDeps) { nodes.add(f.id); for (const d of f.dependencies) nodes.add(d); }
  const edges = withDeps.reduce((s, f) => s + f.dependencies.length, 0);
  if (nodes.size > 25 || edges > 40) {
    return ['기능이 많아 표로 적는다(다이어그램 복잡도 상한 25/40).', '', '| 기능 | 의존하는 기능 |', '|---|---|', ...withDeps.map((f) => `| ${f.id} ${esc(f.name)} | ${f.dependencies.join(', ')} |`)].join('\n');
  }
  const label = (id: string) => { const f = active.find((x) => x.id === id); return `${id}["${id} ${(f?.name ?? '').replace(/["\\]/g, ' ')}"]`; };
  return ['```mermaid', 'flowchart LR', ...[...nodes].map((n) => `  ${label(n)}`), ...withDeps.flatMap((f) => f.dependencies.map((d) => `  ${f.id} --> ${d}`)), '```'].join('\n');
}

export function renderFeaturesIndex(ws: CurrentWorkspace): string {
  const active = ws.features.features.filter((f) => f.status !== 'REMOVED');
  const removed = ws.features.features.filter((f) => f.status === 'REMOVED');
  const row = (f: FeatureSummary) => {
    const fa = ws.featureAnalyses.get(f.id);
    return `| <a id="${f.id.toLowerCase()}"></a>[${f.id}](${featureDocName(f.id, f.slug)}) | ${esc(f.name)} | ${f.importance} | ${f.status} | ${f.entryPoints.map(code).join(', ') || '-'} | ${fa ? esc(fa.summary).slice(0, 160) : esc(f.summary)} |`;
  };
  const byImportance = (imp: string) => active.filter((f) => f.importance === imp);
  const sections = [
    { id: 'summary', body: ['## 한 줄 요약', '', `기능 ${active.length}개(CORE ${byImportance('CORE').length} · SUPPORTING ${byImportance('SUPPORTING').length} · INFRA ${byImportance('INFRA').length}). 각 기능의 흐름·다이어그램·실패 지점은 개별 문서에 있다.`].join('\n') },
    { id: 'list', body: ['## 기능 목록', '', '| ID | 이름 | 중요도 | 상태 | 진입점 | 요약 |', '|---|---|---|---|---|---|', ...active.map(row)].join('\n') },
    { id: 'dependencies', body: ['## 기능 간 의존', '', renderDependencies(active)].join('\n') },
  ];
  if (removed.length) sections.push({ id: 'removed', body: ['## 제거된 기능', '', '| ID | 이름 | 요약 |', '|---|---|---|', ...removed.map((f) => `| ${f.id} | ${esc(f.name)} | ${esc(f.summary)} |`), '', '변경 사유는 [20_CHANGELOG](20_CHANGELOG.md)를 본다.'].join('\n') });
  if (ws.features.excludedCandidates.length) sections.push({ id: 'excluded', body: ['## 기능으로 세지 않은 것', '', ...ws.features.excludedCandidates.map((e) => `- ${e.name} — ${e.reason}`)].join('\n') });
  return assembleDoc('기능 목록', sections);
}

export function renderApiDoc(ws: CurrentWorkspace): string {
  const eps = [...ws.api.endpoints].sort((a, b) => `${a.host}${a.path}${a.method}`.localeCompare(`${b.host}${b.path}${b.method}`));
  const hosts = [...new Set(eps.map((e) => e.host))];
  const sections = [
    { id: 'summary', body: ['## 한 줄 요약', '', `HTTP 엔드포인트 ${eps.length}개, 호스트 ${hosts.join(' / ') || '-'}. 인증·부작용·오류를 표로 정리했다.`].join('\n') },
    { id: 'table', body: ['## 엔드포인트', '', '| Method | Path | Host | 호출자 | 인증 | 요청 | 응답 | 오류 | 기능 | 코드 |', '|---|---|---|---|---|---|---|---|---|---|', ...eps.map((e) => `| ${e.method} | ${code(e.path)} | ${e.host} | ${esc(e.caller)} | ${esc(e.authentication)} | ${esc(e.request)} | ${esc(e.response)} | ${esc(e.errors.join('; '))} | ${e.featureIds.join(', ')} | ${code(e.file)}${e.symbol ? ` ${esc(e.symbol)}` : ''} |`)].join('\n') },
    { id: 'details', body: ['## 상세', '', ...eps.map((e) => [`### ${e.method} ${e.path}`, '', `- 검증: ${e.validation.join('; ') || '-'}`, `- 부작용: ${e.sideEffects.join('; ') || '-'}`, `- DB 변경: ${e.dbChanges.join('; ') || '-'}`, `- 상태: ${e.status} · 근거: ${evid(e.evidence)}`].join('\n'))].join('\n\n') },
    { id: 'other', body: ['## HTTP 밖의 인터페이스', '', claimRows(ws.api.otherInterfaces)].join('\n') },
  ];
  if (ws.api.unknowns.length) sections.push({ id: 'unknowns', body: ['## 확인하지 못한 것', '', ...ws.api.unknowns.map((u) => `- ${u}`)].join('\n') });
  sections.push({ id: 'related', body: ['## 관련 문서', '', related(['09_FEATURES.md', '07_DATA_MODEL.md', '13_SECURITY.md'])].join('\n') });
  return assembleDoc('API', sections);
}

export function renderDataDoc(ws: CurrentWorkspace): string {
  const d = ws.data;
  const sections: { id: string; body: string }[] = [
    { id: 'summary', body: ['## 한 줄 요약', '', `엔티티 ${d.entities.length}개, 관계 ${d.relations.length}개, DTO ${d.dtos.length}개.`].join('\n') },
  ];
  for (const diag of d.diagrams) sections.push(renderDiagramSection(diag));
  sections.push({ id: 'entities', body: ['## 엔티티', '', ...d.entities.map((e) => [`### ${e.name}${e.table ? ` (${e.table})` : ''}`, '', `코드: ${code(e.file)} · 키: ${e.keys.join(', ') || '-'} · 인덱스: ${e.indexes.join(', ') || '-'} · 상태: ${e.status}`, '', '| 필드 | 타입 | 제약 |', '|---|---|---|', ...e.fields.map((f) => `| ${esc(f.name)} | ${esc(f.type)} | ${esc(f.constraints.join(', '))} |`)].join('\n'))].join('\n\n') });
  sections.push({ id: 'relations', body: ['## 관계', '', d.relations.length ? ['| From | To | 카디널리티 | 상태 |', '|---|---|---|---|', ...d.relations.map((r) => `| ${r.from} | ${r.to} | ${r.cardinality} | ${r.status} |`)].join('\n') : '_(없음)_'].join('\n') });
  sections.push({ id: 'dtos', body: ['## DTO', '', d.dtos.length ? ['| 이름 | 파일 | 사용 기능 | 상태 |', '|---|---|---|---|', ...d.dtos.map((x) => `| ${esc(x.name)} | ${code(x.file)} | ${x.usedBy.join(', ')} | ${x.status} |`)].join('\n') : '_(없음)_'].join('\n') });
  sections.push({ id: 'migrations', body: ['## 마이그레이션·트랜잭션', '', '### 마이그레이션', '', claimRows(d.migrations), '', '### 트랜잭션', '', claimRows(d.transactions)].join('\n') });
  if (d.unknowns.length) sections.push({ id: 'unknowns', body: ['## 확인하지 못한 것', '', ...d.unknowns.map((u) => `- ${u}`)].join('\n') });
  sections.push({ id: 'related', body: ['## 관련 문서', '', related(['08_API.md', '09_FEATURES.md', '02_ARCHITECTURE.md'])].join('\n') });
  return assembleDoc('데이터 모델', sections);
}

/** append-only: 기존 문서의 FAIL 섹션은 유지하고 새 id만 붙인다(갱신된 기록은 같은 id 섹션을 교체). */
export function renderFailureHistory(ws: CurrentWorkspace): string {
  const fs = ws.failures.failures;
  const sections: { id: string; body: string }[] = [
    { id: 'summary', body: ['## 한 줄 요약', '', `기록된 실패·workaround ${fs.length}건. 과거 기록은 코드에서 사라져도 보존한다.`, '', '| ID | 제목 | 원인 확신 | 현재 workaround | 최종 해결 |', '|---|---|---|---|---|', ...fs.map((f) => `| [${f.id}](#${f.id.toLowerCase()}) | ${esc(f.title)} | ${f.causeConfidence} | ${esc(f.currentWorkaround || '-')} | ${esc(f.finalSolution || '-')} |`)].join('\n') },
  ];
  for (const f of fs) {
    sections.push({ id: f.id, body: [
      `## <a id="${f.id.toLowerCase()}"></a>${f.id} ${f.title}`, '',
      `**문제:** ${f.problem}`, '', `**시도:** ${f.attempt || '-'}`, '', `**증상:** ${f.symptom}`, '', `**원인 [${f.causeConfidence}]:** ${f.cause}`, '',
      f.failedSolutions.length ? `**실패한 해결:**\n${f.failedSolutions.map((s) => `- ${s}`).join('\n')}` : '**실패한 해결:** -', '',
      `**현재 workaround:** ${f.currentWorkaround || '-'}`, '', `**최종 해결:** ${f.finalSolution || '-'}`, '',
      f.recurrenceProcedure.length ? `**재발 시 확인 절차:**\n${f.recurrenceProcedure.map((s, i) => `${i + 1}. ${s}`).join('\n')}` : '', '',
      `**장기 해결:** ${f.longTermSolution || '-'}`, '',
      `관련 파일: ${f.relatedFiles.map(code).join(', ') || '-'} · 관련 커밋: ${f.relatedCommits.map(code).join(', ') || '-'} · 근거 종류: ${f.sources.join(', ')} · 기록일: ${f.discoveredAt}`,
    ].join('\n') });
  }
  if (ws.failures.unknowns.length) sections.push({ id: 'unknowns', body: ['## 확인하지 못한 것', '', ...ws.failures.unknowns.map((u) => `- ${u}`)].join('\n') });
  return assembleDoc('실패 이력', sections);
}

export function renderTroubleshooting(ws: CurrentWorkspace, journal: Journal): string {
  const items = journal.troubleshooting;
  const sections: { id: string; body: string }[] = [
    { id: 'summary', body: ['## 한 줄 요약', '', items.length ? `장애 재발 시 바로 확인할 항목 ${items.length}개. 증상 → 원인 → 확인 방법 → 해결 → 관련 코드 순서다.` : '아직 기록된 트러블슈팅 항목이 없다. 실패 이력이 쌓이면 여기에 누적된다.'].join('\n') },
  ];
  for (const t of items) {
    sections.push({ id: t.id, body: [`## ${t.id} ${t.symptom}`, '', `**원인 [${t.status}]:** ${t.cause}`, '', '**확인 방법:**', ...t.howToConfirm.map((s, i) => `${i + 1}. ${s}`), '', '**해결:**', ...t.resolution.map((s) => `- ${s}`), '', `관련 코드: ${t.relatedCode.map(code).join(', ') || '-'} · 기록일: ${t.addedAt}`].join('\n') });
  }
  sections.push({ id: 'related', body: ['## 관련 문서', '', related(['11_FAILURE_HISTORY.md', '10_ERROR_HANDLING.md', '16_DEPLOYMENT.md'])].join('\n') });
  return assembleDoc('트러블슈팅', sections);
}

export function renderTechDebt(ws: CurrentWorkspace): string {
  const area = ws.operations.techDebt;
  const byClass = (c: string) => area.items.filter((i) => i.classification === c);
  const table = (items: typeof area.items) => items.length ? ['| ID | 제목 | 설명 | 권고 | 기능 | 근거 |', '|---|---|---|---|---|---|', ...items.map((i) => `| ${i.id} | ${esc(i.title)} | ${esc(i.description)} | ${esc(i.recommendation)} | ${i.featureIds.join(', ') || '-'} | ${esc(evid(i.evidence))} |`)].join('\n') : '_(없음)_';
  return assembleDoc('기술 부채', [
    { id: 'summary', body: ['## 한 줄 요약', '', area.summary, '', `확인된 문제 ${byClass('CONFIRMED_ISSUE').length} · 잠재 위험 ${byClass('POTENTIAL_RISK').length} · 개선 제안 ${byClass('IMPROVEMENT').length}`].join('\n') },
    { id: 'confirmed', body: ['## 확인된 문제(CONFIRMED_ISSUE)', '', table(byClass('CONFIRMED_ISSUE'))].join('\n') },
    { id: 'potential', body: ['## 잠재 위험(POTENTIAL_RISK)', '', table(byClass('POTENTIAL_RISK'))].join('\n') },
    { id: 'improvement', body: ['## 개선 제안(IMPROVEMENT)', '', table(byClass('IMPROVEMENT'))].join('\n') },
    { id: 'related', body: ['## 관련 문서', '', related(['14_PERFORMANCE.md', '13_SECURITY.md', '10_ERROR_HANDLING.md', '19_UNKNOWN_AND_TODO.md'])].join('\n') },
  ]);
}

/** 검증 루프가 끝난 뒤 남은 LLM 지적을 19_UNKNOWN_AND_TODO에 붙인다(발행 시 사람이 확인할 목록). */
export function renderResidualSection(fixRequired: { doc: string; section?: string; issue: string }[]): { id: string; body: string } {
  const rows = fixRequired.map((f) => `| ${f.doc}${f.section ? ` #${f.section}` : ''} | ${esc(f.issue.replace(/ section=[\w:-]+$/, ''))} |`);
  return { id: 'residual', body: ['## 검증 잔여 지적(사람 확인 필요)', '', `검증 루프가 최대 반복 후에도 해결하지 못한 LLM 검증자의 지적 ${fixRequired.length}건. 코드가 맞고 문서가 틀렸을 수도, 지적이 틀렸을 수도 있다 — 다음 \`문서화\` 실행 전에 확인한다.`, '', '| 문서 | 지적 |', '|---|---|', ...rows].join('\n') };
}

export function renderUnknowns(ws: CurrentWorkspace): string {
  const groups: [string, string[]][] = [
    ['Inventory', ws.inventory.unknowns], ['Architecture', ws.architecture.unknowns], ['기능 발견', ws.features.unknowns],
    ...[...ws.featureAnalyses.values()].map((fa): [string, string[]] => [`${fa.feature.id} ${fa.feature.name}`, fa.unknowns]),
    ['데이터 모델', ws.data.unknowns], ['API', ws.api.unknowns], ['실패 이력', ws.failures.unknowns],
    ['오류 처리', ws.operations.errorHandling.unknowns], ['보안', ws.operations.security.unknowns], ['성능', ws.operations.performance.unknowns], ['기술 부채', ws.operations.techDebt.unknowns],
  ];
  const nonEmpty = groups.filter(([, u]) => u.length);
  const total = nonEmpty.reduce((s, [, u]) => s + u.length, 0);
  const potential = [...ws.featureAnalyses.values()].flatMap((fa) => fa.failurePoints.filter((p) => p.status === 'POTENTIAL_ISSUE').map((p) => `${fa.feature.id}: ${p.where} — ${p.condition} (${p.handling})`));
  return assembleDoc('확인하지 못한 것과 TODO', [
    { id: 'summary', body: ['## 한 줄 요약', '', total ? `분석이 확인하지 못한 항목 ${total}개. 새 개발자가 코드를 고치기 전에 직접 확인해야 하는 목록이다.` : '분석 단계에서 UNKNOWN으로 남긴 항목이 없다.'].join('\n') },
    { id: 'unknowns', body: ['## UNKNOWN 목록', '', ...(nonEmpty.length ? nonEmpty.map(([g, u]) => [`### ${g}`, '', ...u.map((x) => `- ${x}`)].join('\n')) : ['_(없음)_'])].join('\n\n') },
    { id: 'potential', body: ['## 기능 분석이 표시한 잠재 문제(POTENTIAL_ISSUE)', '', ...(potential.length ? potential.map((p) => `- ${p}`) : ['_(없음)_'])].join('\n') },
    { id: 'related', body: ['## 관련 문서', '', related(['17_TECH_DEBT.md', '11_FAILURE_HISTORY.md'])].join('\n') },
  ]);
}

export function renderChangelog(journal: Journal): string {
  const byDate = new Map<string, Journal['changelog']>();
  for (const c of journal.changelog) byDate.set(c.date, [...(byDate.get(c.date) ?? []), c]);
  const dates = [...byDate.keys()].sort().reverse();
  const sections: { id: string; body: string }[] = [
    { id: 'summary', body: ['## 한 줄 요약', '', `프로젝트 구조/동작/운영에 의미 있는 구현 변화 ${journal.changelog.length}건. Git 로그 전체가 아니라 문서화 실행이 판정한 변화만 기록한다.`].join('\n') },
  ];
  for (const date of dates) {
    sections.push({ id: `d-${date}`, body: [`## ${date}`, '', ...byDate.get(date)!.map((c) => [`### ${c.title}`, '', `분류: ${c.classification.join(' / ')} (${c.significance}) · 실행: ${c.run}`, '', c.description, '', c.impact.length ? `**영향:**\n${c.impact.map((i) => `- ${i}`).join('\n')}` : '', c.relatedDocs.length ? `**관련 문서:**\n${c.relatedDocs.map((d) => `- [${d}](${d})`).join('\n')}` : '', c.commits.length ? `관련 커밋: ${c.commits.map(code).join(', ')}` : ''].filter((x) => x !== '').join('\n\n'))].join('\n\n') });
  }
  return assembleDoc('구현 변경 기록', sections);
}

export function renderDirectoryStructure(ws: CurrentWorkspace, fileTree: string): string {
  const dirs = ws.inventory.directories;
  return assembleDoc('디렉터리 구조', [
    { id: 'summary', body: ['## 한 줄 요약', '', `디렉터리 ${dirs.length}개의 역할. 파일 트리는 문서화 시점의 스냅샷이다.`].join('\n') },
    { id: 'roles', body: ['## 디렉터리 역할', '', '| 경로 | 역할 | 상태 |', '|---|---|---|', ...dirs.map((d) => `| ${code(d.path)} | ${esc(d.role)} | ${d.status} |`)].join('\n') },
    { id: 'tree', body: ['## 파일 트리(분석 대상만)', '', '```text', fileTree, '```'].join('\n') },
    { id: 'related', body: ['## 관련 문서', '', related(['01_PROJECT_OVERVIEW.md', '02_ARCHITECTURE.md'])].join('\n') },
  ]);
}

export function renderReadme(ws: CurrentWorkspace, journal: Journal, today: string): string {
  const docs = [
    ['00_EXECUTIVE_SUMMARY.md', '5~10분 안에 프로젝트를 이해하기 위한 요약'], ['01_PROJECT_OVERVIEW.md', '목적·기술 스택·구성'], ['02_ARCHITECTURE.md', '컴포넌트·계층·런타임·제어 흐름'],
    ['03_DIRECTORY_STRUCTURE.md', '디렉터리 역할과 파일 트리'], ['04_SETUP_AND_RUN.md', '설치·실행·디버깅'], ['05_CONFIGURATION.md', '설정 키와 환경'], ['06_DEPENDENCIES.md', '의존성'],
    ['07_DATA_MODEL.md', '엔티티·관계·ER'], ['08_API.md', '엔드포인트 표'], ['09_FEATURES.md', '기능 목록(개별 문서는 features/)'], ['10_ERROR_HANDLING.md', '오류 처리'],
    ['11_FAILURE_HISTORY.md', '과거 실패와 workaround(누적)'], ['12_TROUBLESHOOTING.md', '장애 재발 시 확인 절차'], ['13_SECURITY.md', '보안'], ['14_PERFORMANCE.md', '성능'],
    ['15_TESTING.md', '테스트'], ['16_DEPLOYMENT.md', '배포'], ['17_TECH_DEBT.md', '기술 부채'], ['18_GLOSSARY.md', '용어'], ['19_UNKNOWN_AND_TODO.md', '확인하지 못한 것'], ['20_CHANGELOG.md', '의미 있는 구현 변경 기록'],
  ];
  const features = ws.features.features.filter((f) => f.status !== 'REMOVED');
  return assembleDoc(`${ws.inventory.project.name} 기술 문서`, [
    { id: 'summary', body: ['## 한 줄 요약', '', ws.inventory.project.description, '', `이 문서들은 doc-harness가 **실제 코드를 근거로** 생성·검증했다(마지막 갱신 ${today}, 문서화 실행 ${journal.changelog.length ? journal.changelog[journal.changelog.length - 1].run : '-'}). 다이어그램은 모두 Mermaid이며 각 문서 안에 원본이 있다. 코드와 문서가 다르면 코드가 맞다 — \`문서화\`를 다시 실행한다.`].join('\n') },
    { id: 'docs', body: ['## 문서', '', '| 문서 | 내용 |', '|---|---|', ...docs.map(([f, d]) => `| [${f}](${f}) | ${d} |`)].join('\n') },
    { id: 'features', body: ['## 기능 문서', '', '| ID | 기능 | 중요도 |', '|---|---|---|', ...features.map((f) => `| [${f.id}](${featureDocName(f.id, f.slug)}) | ${esc(f.name)} | ${f.importance} |`)].join('\n') },
    { id: 'legend', body: ['## 상태 표기', '', '| 상태 | 뜻 |', '|---|---|', '| CONFIRMED | 코드에서 직접 확인 |', '| INFERRED | 정황·문서·이력에서 추론 |', '| UNKNOWN | 확인하지 못함 |', '| POSSIBLE_LEGACY | 더 이상 쓰이지 않는 것으로 보임 |', '| POTENTIAL_ISSUE | 문제 가능성 관찰 |'].join('\n') },
  ]);
}

export function renderAdrDocs(ws: CurrentWorkspace): { name: string; md: string }[] {
  return ws.architecture.decisions.map((d, i) => ({
    name: adrDocName(i + 1, d.title),
    md: assembleDoc(`ADR-${String(i + 1).padStart(3, '0')} ${d.title}`, [
      { id: 'summary', body: ['## 결정', '', d.decision, '', `상태: ${d.status}`].join('\n') },
      { id: 'rationale', body: ['## 근거', '', d.rationale, '', `코드 근거: ${evid(d.evidence)}`].join('\n') },
      { id: 'related', body: ['## 관련 문서', '', related(['../02_ARCHITECTURE.md'])].join('\n') },
    ]),
  }));
}

/** 템플릿 문서 하나를 이름으로 렌더한다. */
export function renderTemplateDoc(name: string, ws: CurrentWorkspace, extras: RenderExtras): string {
  switch (name) {
    case 'README.md': return renderReadme(ws, extras.journal, extras.today);
    case '03_DIRECTORY_STRUCTURE.md': return renderDirectoryStructure(ws, extras.fileTree);
    case '07_DATA_MODEL.md': return renderDataDoc(ws);
    case '08_API.md': return renderApiDoc(ws);
    case '09_FEATURES.md': return renderFeaturesIndex(ws);
    case '11_FAILURE_HISTORY.md': return renderFailureHistory(ws);
    case '12_TROUBLESHOOTING.md': return renderTroubleshooting(ws, extras.journal);
    case '17_TECH_DEBT.md': return renderTechDebt(ws);
    case '19_UNKNOWN_AND_TODO.md': return renderUnknowns(ws);
    case '20_CHANGELOG.md': return renderChangelog(extras.journal);
    default: throw new Error(`템플릿 문서가 아니다: ${name}`);
  }
}

/** 문서의 섹션 id 목록(검증·baseline용). */
export function sectionIds(md: string): string[] {
  return parseSections(md).sections.map((s) => s.id);
}
