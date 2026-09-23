// 스키마를 통과하는 최소 산출물 생성기. Fake 러너 테스트에서 쓴다.
import type { ApiModel, Architecture, Baseline, Classification, DataModel, Diagram, FailuresFile, FeatureAnalysis, FeatureDelta, FeatureSummary, FeaturesFile, Inventory, Operations, OperationsArea, Verification } from '../../src/types.js';

const ev = (file: string, symbol?: string) => [{ file, ...(symbol ? { symbol } : {}) }];
const claim = (text: string, file = 'Api/Program.cs') => ({ text, status: 'CONFIRMED' as const, evidence: ev(file) });

export function sampleInventory(): Inventory {
  return {
    project: { name: 'Mini', description: '테스트용 미니 블로그', status: 'CONFIRMED', evidence: ev('Mini.Api.csproj') },
    languages: [claim('C#', 'Mini.Api.csproj'), claim('TypeScript', 'Web/package.json')],
    frameworks: [claim('ASP.NET Core', 'Mini.Api.csproj')], runtimes: [claim('.NET 10', 'Mini.Api.csproj')],
    entryPoints: [claim('Api/Program.cs')],
    directories: [{ path: 'Api', role: '백엔드', status: 'CONFIRMED' }, { path: 'Web', role: 'SPA', status: 'CONFIRMED' }],
    configs: [claim('Api/appsettings.json', 'Api/appsettings.json')], environments: [], build: [], dependencies: [claim('react 19', 'Web/package.json')],
    databases: [claim('EF Core AppDbContext', 'Api/Infrastructure/Data/AppDbContext.cs')], caches: [], queues: [], externalSystems: [], containers: [], ci: [], tests: [], scripts: [], migrations: [], staticResources: [],
    tooling: [], unknowns: [],
  };
}

export function sampleDiagram(id = 'ARCH_SYSTEM', type: Diagram['type'] = 'architecture'): Diagram {
  return {
    id, type, title: '시스템 구조', summary: 'PostEndpoints가 AppDbContext를 통해 저장한다.',
    mermaid: type === 'sequence' ? 'sequenceDiagram\n  participant PostEndpoints\n  participant AppDbContext\n  PostEndpoints->>AppDbContext: SaveChanges' : 'flowchart LR\n  PostEndpoints --> AppDbContext',
    details: '상세 설명.', nodes: [{ node: 'PostEndpoints', code: 'Api/Features/Posts/PostEndpoints.cs' }, { node: 'AppDbContext', code: 'Api/Infrastructure/Data/AppDbContext.cs' }], status: 'CONFIRMED',
  };
}

export function sampleArchitecture(): Architecture {
  return {
    components: [
      { id: 'PostEndpoints', name: 'PostEndpoints', path: 'Api/Features/Posts/PostEndpoints.cs', layer: 'api', responsibilities: '글 API', status: 'CONFIRMED', evidence: ev('Api/Features/Posts/PostEndpoints.cs') },
      { id: 'AuthEndpoints', name: 'AuthEndpoints', path: 'Api/Features/Auth/AuthEndpoints.cs', layer: 'api', responsibilities: '로그인', status: 'CONFIRMED', evidence: ev('Api/Features/Auth/AuthEndpoints.cs') },
      { id: 'AppDbContext', name: 'AppDbContext', path: 'Api/Infrastructure/Data/AppDbContext.cs', layer: 'data', responsibilities: 'EF Core', status: 'CONFIRMED', evidence: ev('Api/Infrastructure/Data/AppDbContext.cs') },
    ],
    relations: [{ from: 'PostEndpoints', to: 'AppDbContext', kind: 'writes', status: 'CONFIRMED', evidence: ev('Api/Features/Posts/PostEndpoints.cs') }],
    runtime: { processes: [claim('단일 ASP.NET Core 프로세스')], threads: [], network: [] },
    storage: [claim('PostgreSQL', 'Api/appsettings.json')], externalSystems: [],
    controlFlows: [{ name: '글 목록 조회', steps: ['GET /api/posts', 'PostEndpoints', 'AppDbContext'], status: 'CONFIRMED', evidence: ev('Api/Features/Posts/PostEndpoints.cs') }],
    diagrams: [sampleDiagram()], decisions: [], unknowns: [],
  };
}

export function sampleFeatureSummary(id = 'F001', slug = 'POST_LIST', files = ['Api/Features/Posts/PostEndpoints.cs']): FeatureSummary {
  return { id, name: '글 목록 조회', slug, summary: '글 목록을 돌려준다', entryPoints: ['GET /api/posts'], relatedFiles: files, dependencies: [], importance: 'CORE', analysisStatus: 'PENDING', status: 'ACTIVE' };
}

export function sampleFeatures(): FeaturesFile {
  return {
    features: [sampleFeatureSummary('F001'), sampleFeatureSummary('F002', 'ADMIN_LOGIN', ['Api/Features/Auth/AuthEndpoints.cs'])],
    excludedCandidates: [{ name: 'doc-harness', reason: '개발 도구' }], unknowns: [],
  };
}

export function sampleFeatureAnalysis(summary: FeatureSummary = sampleFeatureSummary()): FeatureAnalysis {
  const file = summary.relatedFiles[0];
  return {
    feature: { ...summary, analysisStatus: 'SUCCESS' },
    summary: `${summary.name} 기능은 ${file}에서 처리된다.`,
    entryPoints: [claim(summary.entryPoints[0] ?? 'n/a', file)],
    executionFlow: [{ step: 1, component: 'PostEndpoints', file, symbol: 'MapPostEndpoints', description: '요청 수신' }, { step: 2, component: 'AppDbContext', file: 'Api/Infrastructure/Data/AppDbContext.cs', description: '조회' }],
    relatedCode: [{ file, symbol: 'MapPostEndpoints', role: 'entry' }],
    dataFlow: [claim('요청 → DbContext → 응답', file)], stateTransitions: [],
    databaseAccess: [{ entity: 'Post', operation: 'SELECT', file: 'Api/Infrastructure/Data/AppDbContext.cs' }],
    externalDependencies: [],
    failurePoints: [{ where: 'PostEndpoints', condition: 'DB 연결 실패', handling: '500', status: 'INFERRED', evidence: ev(file) }],
    edgeCases: [], logging: [],
    diagrams: [sampleDiagram(`${summary.id}_SEQUENCE`, 'sequence')],
    unknowns: [], evidence: ev(file), history: [],
  };
}

export function sampleData(): DataModel {
  return {
    entities: [
      { name: 'Post', table: 'posts', file: 'Api/Infrastructure/Data/AppDbContext.cs', fields: [{ name: 'Id', type: 'int', constraints: ['PK'] }, { name: 'Title', type: 'string', constraints: [] }], keys: ['Id'], indexes: [], status: 'CONFIRMED', evidence: ev('Api/Infrastructure/Data/AppDbContext.cs') },
      { name: 'Tag', table: 'tags', file: 'Api/Infrastructure/Data/AppDbContext.cs', fields: [{ name: 'Id', type: 'int', constraints: ['PK'] }], keys: ['Id'], indexes: [], status: 'CONFIRMED', evidence: ev('Api/Infrastructure/Data/AppDbContext.cs') },
    ],
    relations: [], dtos: [{ name: 'LoginRequest', file: 'Api/Features/Auth/AuthEndpoints.cs', usedBy: ['F002'], status: 'CONFIRMED' }], migrations: [], transactions: [],
    diagrams: [{ ...sampleDiagram('DATA_ER', 'er'), mermaid: 'erDiagram\n  Post ||--o{ Tag : has', nodes: [{ node: 'Post', code: 'Api/Infrastructure/Data/AppDbContext.cs' }, { node: 'Tag', code: 'Api/Infrastructure/Data/AppDbContext.cs' }] }], unknowns: [],
  };
}

export function sampleApi(): ApiModel {
  const ep = (method: string, p: string, file: string, featureIds: string[]) => ({ method, path: p, host: 'admin', caller: 'SPA', file, request: '-', response: 'JSON', validation: [], authentication: '세션', sideEffects: [], dbChanges: [], errors: ['401'], featureIds, status: 'CONFIRMED' as const, evidence: ev(file) });
  return {
    endpoints: [ep('GET', '/api/posts', 'Api/Features/Posts/PostEndpoints.cs', ['F001']), ep('POST', '/api/posts', 'Api/Features/Posts/PostEndpoints.cs', ['F001']), ep('POST', '/api/auth/login', 'Api/Features/Auth/AuthEndpoints.cs', ['F002'])],
    otherInterfaces: [], unknowns: [],
  };
}

export function sampleFailures(): FailuresFile {
  return {
    failures: [{ id: 'FAIL001', title: '로그인 속도 제한 없음', problem: 'TODO가 남아 있다', attempt: '-', symptom: '무제한 시도', cause: '미구현', causeConfidence: 'CONFIRMED', failedSolutions: [], currentWorkaround: '없음', finalSolution: '미정', relatedFiles: ['Api/Features/Auth/AuthEndpoints.cs'], relatedCommits: [], recurrenceProcedure: ['로그인을 반복 시도한다'], longTermSolution: '속도 제한 추가', troubleshootingWorthy: true, sources: ['code'], discoveredAt: '2026-09-23' }],
    unknowns: [],
  };
}

export function sampleOperationsArea(prefix = 'OPS'): OperationsArea {
  return { items: [{ id: `${prefix}001`, title: '샘플 항목', classification: 'POTENTIAL_RISK', description: '설명', recommendation: '권고', evidence: ev('Api/Program.cs'), featureIds: ['F001'] }], summary: '요약', unknowns: [] };
}

export function sampleOperations(): Operations {
  return { errorHandling: sampleOperationsArea('ERR'), security: sampleOperationsArea('SEC'), performance: sampleOperationsArea('PERF'), techDebt: sampleOperationsArea('DEBT') };
}

export function sampleVerification(overrides: Partial<Verification> = {}): Omit<Verification, 'mermaidParser' | 'iterations' | 'passed'> {
  return { score: { coverage: 90, accuracy: 95 }, hallucinations: [], missingItems: [], incorrectRelations: [], diagramIssues: [], unsupportedClaims: [], fixRequired: [], ...overrides };
}

export function sampleClassification(files: string[]): Classification {
  return { items: files.map((file) => ({ file, classifications: ['FEATURE_CHANGE'], significance: 'MINOR', possibleFeatures: [], rationale: '샘플' })), summary: '샘플 분류', changelogCandidates: [] };
}

export function sampleFeatureDelta(): FeatureDelta {
  return { newFeatures: [], changedFeatureIds: [], removedFeatures: [], unknowns: [] };
}

export function sampleBaseline(): Baseline {
  return { documentationVersion: 1, lastSuccessfulRun: 'run-0001', lastSuccessfulAt: '2026-09-23T00:00:00Z', baselineCommit: 'abc', workingTreeFingerprint: 'fp', files: {}, features: {}, documents: {}, diagrams: {} };
}
