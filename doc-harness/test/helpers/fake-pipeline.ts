// 파이프라인 한 바퀴를 Fake로 돌리기 위한 스키마별 응답 생성기.
import { cpSync, mkdirSync } from 'node:fs';
import { mkdtemp } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import type { ClaudeRequest } from '../../src/claude.js';
import { git } from '../../src/git.js';
import type { FeatureSummary } from '../../src/types.js';
import { miniProjectRoot } from './context.js';
import { sampleApi, sampleArchitecture, sampleData, sampleFailures, sampleFeatureAnalysis, sampleFeatureSummary, sampleFeatures, sampleInventory, sampleOperationsArea, sampleVerification } from './samples.js';

export interface FakeState { features: FeatureSummary[]; newFeatureFiles: string[] }

/** req.schemaName에 맞는 최소 응답. 상태(기능 목록)를 공유해 신규 기능을 흉내 낼 수 있다. */
export function fakeResponder(state: FakeState = { features: sampleFeatures().features, newFeatureFiles: [] }) {
  return (req: ClaudeRequest): unknown => {
    switch (req.schemaName) {
      case 'inventory': return sampleInventory();
      case 'architecture': return sampleArchitecture();
      case 'features': return { ...sampleFeatures(), features: state.features };
      case 'feature': {
        const id = /feature:(F\d{3})/.exec(req.id)![1];
        const s = state.features.find((f) => f.id === id) ?? sampleFeatureSummary(id);
        return sampleFeatureAnalysis(s);
      }
      case 'data': return sampleData();
      case 'api': return sampleApi();
      case 'failures': return sampleFailures();
      case 'operations_area': return sampleOperationsArea(req.id.split(':')[1]?.slice(0, 4).toUpperCase() ?? 'OPS');
      case 'document': {
        const name = req.id.replace(/^doc:/, '').replace(/:fix.*$/, '');
        // 실제 LLM처럼 입력 발췌의 이름들을 본문에 언급한다(교차 일관성 검사가 컴포넌트 이름을 요구한다).
        const names = [...new Set([...req.prompt.matchAll(/"name": "([^"]+)"/g)].map((m) => m[1]))].join(', ');
        return { title: name, oneLiner: `${name} 한 줄`, sections: [{ id: 'summary', heading: '한 줄 요약', body: `${name} 요약 본문. 등장: ${names}`, diagrams: [], unchanged: false }, { id: 'related', heading: '관련 문서', body: '[09_FEATURES](09_FEATURES.md)', diagrams: [], unchanged: false }], relatedDocs: [], unknowns: [] };
      }
      case 'verification': return sampleVerification();
      case 'consistency': return { issues: [], summary: '모순 없음' };
      case 'classification': {
        const files = [...req.prompt.matchAll(/^### (?:ADDED|MODIFIED|DELETED|RENAMED|UNTRACKED) (\S+)/gm)].map((m) => m[1]);
        return { items: files.map((file) => ({ file, classifications: [state.newFeatureFiles.includes(file) ? 'NEW_FEATURE' : file.endsWith('.md') ? 'DOCUMENTATION_ONLY' : 'FEATURE_CHANGE'], significance: file.endsWith('.md') ? 'TRIVIAL' : 'MINOR', possibleFeatures: [], rationale: 'fake' })), summary: 'fake 분류', changelogCandidates: [] };
      }
      case 'feature_delta': {
        const reserved = /예약된 신규 id\n\n([^\n]+)/.exec(req.prompt)?.[1].split(', ') ?? ['F999'];
        const newFeatures = state.newFeatureFiles.map((file, i) => ({ ...sampleFeatureSummary(reserved[i], `NEW_${i}`, [file]), name: `신규 기능 ${i}` }));
        state.features = [...state.features, ...newFeatures];
        return { newFeatures, changedFeatureIds: [], removedFeatures: [], unknowns: [] };
      }
      case 'failure_delta': return { newFailures: [], updatedFailures: [], troubleshooting: [], changelog: [{ title: '증분 변경', classification: ['FEATURE_CHANGE'], description: 'd', impact: [], relatedDocs: [], commits: [], significance: 'MINOR' }], unknowns: [] };
      case 'diagram_update': return { decision: 'UNCHANGED', mermaid: /기존 Mermaid\(문서 원본\)\n\n```mermaid\n([\s\S]*?)\n```/.exec(req.prompt)?.[1] ?? '', changedEdges: [], nodes: [], reason: '변경 없음' };
      default: throw new Error(`fakeResponder: unsupported schema ${req.schemaName}`);
    }
  };
}

/** mini-project를 임시 git 저장소로 복사한다. */
export async function tempMiniRepo(): Promise<string> {
  const root = await mkdtemp(path.join(os.tmpdir(), 'dh-pipe-'));
  cpSync(miniProjectRoot, root, { recursive: true });
  mkdirSync(path.join(root, 'docs'), { recursive: true });
  await git(root, ['init', '-q', '-b', 'main']);
  await git(root, ['config', 'user.email', 't@example.com']);
  await git(root, ['config', 'user.name', 't']);
  await git(root, ['add', '-A']);
  await git(root, ['commit', '-q', '-m', '추가: 초기']);
  return root;
}
