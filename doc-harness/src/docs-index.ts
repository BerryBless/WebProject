// 생성 문서의 목록과 각 문서가 어떤 워크스페이스 산출물에 의존하는지(입력 선언). depgraph·증분 갱신·검증이 공유한다.
import type { ChangeClass } from './types.js';

export type WorkspaceInput = 'inventory' | 'architecture' | 'features' | 'featureAnalyses' | 'data' | 'api' | 'failures' | 'operations' | 'journal' | 'truth';

/** LLM이 서술을 쓰는 문서. 입력 해시가 바뀔 때만 다시 생성한다. */
export const NARRATIVE_DOCS = [
  '00_EXECUTIVE_SUMMARY.md', '01_PROJECT_OVERVIEW.md', '02_ARCHITECTURE.md', '04_SETUP_AND_RUN.md', '05_CONFIGURATION.md',
  '06_DEPENDENCIES.md', '10_ERROR_HANDLING.md', '13_SECURITY.md', '14_PERFORMANCE.md', '15_TESTING.md', '16_DEPLOYMENT.md', '18_GLOSSARY.md',
] as const;

/** 워크스페이스 JSON에서 결정적으로 렌더하는 문서(비용 0, 환각 0). 매 Run 다시 렌더한다. */
export const TEMPLATE_DOCS = [
  'README.md', '03_DIRECTORY_STRUCTURE.md', '07_DATA_MODEL.md', '08_API.md', '09_FEATURES.md', '11_FAILURE_HISTORY.md',
  '12_TROUBLESHOOTING.md', '17_TECH_DEBT.md', '19_UNKNOWN_AND_TODO.md', '20_CHANGELOG.md',
] as const;

export const DOC_INPUTS: Record<string, WorkspaceInput[]> = {
  'README.md': ['inventory', 'features', 'journal'],
  '00_EXECUTIVE_SUMMARY.md': ['inventory', 'architecture', 'features', 'data', 'api', 'failures', 'operations'],
  '01_PROJECT_OVERVIEW.md': ['inventory', 'features'],
  '02_ARCHITECTURE.md': ['architecture', 'inventory'],
  '03_DIRECTORY_STRUCTURE.md': ['inventory'],
  '04_SETUP_AND_RUN.md': ['inventory'],
  '05_CONFIGURATION.md': ['inventory', 'truth'],
  '06_DEPENDENCIES.md': ['inventory'],
  '07_DATA_MODEL.md': ['data'],
  '08_API.md': ['api'],
  '09_FEATURES.md': ['features', 'featureAnalyses'],
  '10_ERROR_HANDLING.md': ['operations', 'featureAnalyses'],
  '11_FAILURE_HISTORY.md': ['failures'],
  '12_TROUBLESHOOTING.md': ['journal', 'failures'],
  '13_SECURITY.md': ['operations', 'architecture'],
  '14_PERFORMANCE.md': ['operations'],
  '15_TESTING.md': ['inventory'],
  '16_DEPLOYMENT.md': ['inventory', 'architecture'],
  '17_TECH_DEBT.md': ['operations'],
  '18_GLOSSARY.md': ['features', 'data', 'architecture'],
  '19_UNKNOWN_AND_TODO.md': ['inventory', 'architecture', 'features', 'featureAnalyses', 'data', 'api', 'failures', 'operations'],
  '20_CHANGELOG.md': ['journal'],
};

/** 변경 분류가 직접 건드리는 문서. features 경유 전파와 별개로 항상 재검증 대상에 넣는다. */
export const CLASS_TO_DOCS: Partial<Record<ChangeClass, string[]>> = {
  CONFIG_CHANGE: ['05_CONFIGURATION.md'],
  DEPENDENCY_CHANGE: ['06_DEPENDENCIES.md'],
  DEPLOYMENT_CHANGE: ['16_DEPLOYMENT.md', '04_SETUP_AND_RUN.md'],
  TEST_CHANGE: ['15_TESTING.md'],
  SECURITY_CHANGE: ['13_SECURITY.md'],
  PERFORMANCE_CHANGE: ['14_PERFORMANCE.md'],
  ERROR_HANDLING_CHANGE: ['10_ERROR_HANDLING.md'],
  ARCHITECTURE_CHANGE: ['02_ARCHITECTURE.md', '01_PROJECT_OVERVIEW.md'],
  API_CHANGE: ['08_API.md'],
  DATA_MODEL_CHANGE: ['07_DATA_MODEL.md'],
  BUG_FIX: ['11_FAILURE_HISTORY.md', '12_TROUBLESHOOTING.md'],
};

/** 변경이 하나라도 있으면 항상 다시 렌더/검토하는 문서. */
export const ALWAYS_DOCS = ['README.md', '00_EXECUTIVE_SUMMARY.md', '20_CHANGELOG.md', '19_UNKNOWN_AND_TODO.md', '09_FEATURES.md'];

export function featureDocName(id: string, slug: string): string {
  return `features/${id}_${slug}.md`;
}

export function adrDocName(index: number, title: string): string {
  const slug = title.replace(/[^\p{L}\p{N}]+/gu, '_').replace(/^_|_$/g, '').slice(0, 40) || 'DECISION';
  return `adr/ADR-${String(index).padStart(3, '0')}_${slug}.md`;
}
