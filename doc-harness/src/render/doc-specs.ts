// 서술 문서(LLM 생성)별 사양: 제목·목적·섹션 골격·입력 발췌·먼저 읽을 파일.
import type { CurrentWorkspace } from '../types.js';

export interface SectionSpec { id: string; heading: string; guidance: string }

export interface DocSpec {
  title: string;
  purpose: string;
  sections: SectionSpec[];
  /** 워크스페이스에서 주입할 발췌(키 → JSON 필드 목록). */
  inputs: (ws: CurrentWorkspace) => Record<string, unknown>;
  /** 에이전트가 먼저 Read할 파일 힌트(경로 패턴 설명). */
  readHints: string;
  phase: string;
}

const common = {
  evidence: { id: 'evidence', heading: '코드 근거', guidance: '이 문서의 주장이 근거하는 파일·심볼 표. 실존 경로만.' },
  caveats: { id: 'caveats', heading: '주의사항', guidance: '수정 전에 알아야 할 함정·제약. 없으면 "특이사항 없음".' },
  related: { id: 'related', heading: '관련 문서', guidance: '다른 생성 문서로의 상대 링크 목록(예: [08_API](08_API.md)).' },
};

const featureIndex = (ws: CurrentWorkspace) => ws.features.features.filter((f) => f.status !== 'REMOVED').map((f) => ({ id: f.id, name: f.name, importance: f.importance, summary: f.summary, entryPoints: f.entryPoints }));
const componentIndex = (ws: CurrentWorkspace) => ws.architecture.components.map((c) => ({ id: c.id, name: c.name, path: c.path, layer: c.layer, responsibilities: c.responsibilities }));
const opsArea = (a: CurrentWorkspace['operations']['security']) => ({ summary: a.summary, items: a.items, unknowns: a.unknowns });

export const DOC_SPECS: Record<string, DocSpec> = {
  '00_EXECUTIVE_SUMMARY.md': {
    title: '경영진 요약(Executive Summary)', phase: 'document',
    purpose: '처음 보는 개발자가 5~10분 안에 프로젝트를 이해하게 한다. 가장 중요한 문서다.',
    sections: [
      { id: 'summary', heading: '한 줄 요약', guidance: '프로젝트 한 줄 설명과 목적.' },
      { id: 'key-points', heading: '핵심 내용', guidance: '핵심 기능(상위 5~8개, 기능 문서 링크), 기술 스택, 런타임 구조, 핵심 DB, 외부 시스템을 표로.' },
      { id: 'architecture', heading: '전체 Architecture', guidance: '시스템 수준 구조를 2~4문장으로. 다이어그램은 02_ARCHITECTURE의 ARCH_SYSTEM을 링크하고 여기서는 새로 그리지 않는다(diagrams는 빈 배열).' },
      { id: 'dataflow', heading: '핵심 Data Flow', guidance: '가장 중요한 데이터 흐름 1~2개를 문장과 표로. 필요하면 dataflow 다이어그램 1개(id EXEC_DATAFLOW).' },
      { id: 'status', heading: '현재 상태', guidance: '무엇이 동작하고 무엇이 미완인지. 알려진 문제, 주요 기술 부채(17_TECH_DEBT 링크), 가장 중요한 실패/workaround(11_FAILURE_HISTORY 링크).' },
      { id: 'before-you-change', heading: '수정 전 반드시 알아야 하는 것', guidance: '신규 개발자가 코드를 고치기 전에 알아야 할 제약·함정 5~10개. 각 항목에 근거 문서 링크.' },
      common.related,
    ],
    inputs: (ws) => ({ project: ws.inventory.project, frameworks: ws.inventory.frameworks, runtimes: ws.inventory.runtimes, databases: ws.inventory.databases, externalSystems: ws.inventory.externalSystems, containers: ws.inventory.containers, components: componentIndex(ws), runtime: ws.architecture.runtime, features: featureIndex(ws), entities: ws.data.entities.map((e) => e.name), endpointCount: ws.api.endpoints.length, failures: ws.failures.failures.map((f) => ({ id: f.id, title: f.title, currentWorkaround: f.currentWorkaround })), confirmedIssues: [ws.operations.security, ws.operations.errorHandling, ws.operations.performance, ws.operations.techDebt].flatMap((a) => a.items.filter((i) => i.classification === 'CONFIRMED_ISSUE').map((i) => ({ id: i.id, title: i.title }))) }),
    readHints: '필요하면 README.md(INFERRED)와 진입점 파일만.',
  },
  '01_PROJECT_OVERVIEW.md': {
    title: '프로젝트 개요', phase: 'document',
    purpose: '무엇을 위한 프로젝트이고 어떤 구성으로 이루어졌는지.',
    sections: [
      { id: 'summary', heading: '한 줄 요약', guidance: '프로젝트 설명.' },
      { id: 'key-points', heading: '핵심 내용', guidance: '목적, 사용자, 구성 요소(프로젝트/패키지 단위), 기술 스택 표.' },
      { id: 'details', heading: '상세 내용', guidance: '각 구성 요소의 역할과 경계, 개발 도구(하네스)가 제품과 분리되어 있다는 사실.' },
      common.evidence, common.caveats, common.related,
    ],
    inputs: (ws) => ({ project: ws.inventory.project, languages: ws.inventory.languages, frameworks: ws.inventory.frameworks, runtimes: ws.inventory.runtimes, directories: ws.inventory.directories, tooling: ws.inventory.tooling, features: featureIndex(ws) }),
    readHints: '솔루션/프로젝트 파일(*.slnx, *.csproj, package.json), README.md(INFERRED).',
  },
  '02_ARCHITECTURE.md': {
    title: '아키텍처', phase: 'document',
    purpose: '컴포넌트·계층·의존·런타임·제어 흐름을 실제 이름으로 설명한다.',
    sections: [
      { id: 'summary', heading: '한 줄 요약', guidance: '구조를 한두 문장으로.' },
      { id: 'components', heading: '컴포넌트와 계층', guidance: '컴포넌트 표(이름·경로·계층·책임). 시스템 다이어그램은 입력 diagrams의 ARCH_SYSTEM을 **그대로** 이 섹션의 diagrams에 넣는다(id·mermaid 변경 금지). 서브시스템 다이어그램이 있으면 함께.' },
      { id: 'runtime', heading: '런타임 구조', guidance: '프로세스·스레드/백그라운드·네트워크(포트·호스트·신뢰 경계)를 표로.' },
      { id: 'control-flows', heading: '주요 제어 흐름', guidance: '대표 흐름을 단계 목록으로. 각 단계에 실제 클래스명.' },
      { id: 'decisions', heading: '설계 결정', guidance: '코드로 확인되는 결정과 근거. adr/ 문서 링크.' },
      common.evidence, common.caveats, common.related,
    ],
    inputs: (ws) => ({ components: ws.architecture.components, relations: ws.architecture.relations, runtime: ws.architecture.runtime, storage: ws.architecture.storage, externalSystems: ws.architecture.externalSystems, controlFlows: ws.architecture.controlFlows, decisions: ws.architecture.decisions, diagrams: ws.architecture.diagrams, unknowns: ws.architecture.unknowns }),
    readHints: '진입점(Program.cs 등)과 컴포넌트 경로.',
  },
  '04_SETUP_AND_RUN.md': {
    title: '설치·실행·디버깅', phase: 'document',
    purpose: '새 개발자가 로컬에서 띄우고 테스트하고 디버깅하는 절차. **실제로 존재하는 명령·스크립트·설정만** 적는다.',
    sections: [
      { id: 'summary', heading: '한 줄 요약', guidance: '준비물과 실행 한 줄.' },
      { id: 'prerequisites', heading: '준비물', guidance: '런타임·도구·버전(매니페스트에서 확인).' },
      { id: 'run', heading: '실행', guidance: '각 구성 요소를 띄우는 명령을 코드 블록으로. 스크립트 이름은 package.json scripts·*.ps1·*.sh에서 확인.' },
      { id: 'test', heading: '테스트', guidance: '테스트 실행 명령과 사전 조건(DB·Docker 등).' },
      { id: 'debug', heading: '디버깅', guidance: '로그 위치, 프로필, 자주 쓰는 진단 명령.' },
      common.evidence, common.caveats, common.related,
    ],
    inputs: (ws) => ({ runtimes: ws.inventory.runtimes, build: ws.inventory.build, scripts: ws.inventory.scripts, tests: ws.inventory.tests, environments: ws.inventory.environments, containers: ws.inventory.containers }),
    readHints: 'package.json(scripts), *.csproj, launchSettings.json, deploy/*.sh, scripts/*.ps1, docs/development.md(INFERRED).',
  },
  '05_CONFIGURATION.md': {
    title: '설정', phase: 'document',
    purpose: '모든 설정 키의 의미·기본값·검증 조건. 값(비밀)은 적지 않는다.',
    sections: [
      { id: 'summary', heading: '한 줄 요약', guidance: '설정 소스(파일·환경변수)와 우선순위.' },
      { id: 'keys', heading: '설정 키', guidance: '키 · 의미 · 기본값 · 필수 여부 · 읽는 코드 표. 아래 정답 목록의 키는 전부 다룬다.' },
      { id: 'validation', heading: '시작 시 검증', guidance: '앱이 시작할 때 검사하는 조건과 실패 시 동작.' },
      common.evidence, common.caveats, common.related,
    ],
    inputs: (ws) => ({ configs: ws.inventory.configs, environments: ws.inventory.environments }),
    readHints: 'appsettings*.json(키만), Options 클래스, Program.cs의 검증 코드, .env.example.',
  },
  '06_DEPENDENCIES.md': {
    title: '의존성', phase: 'document',
    purpose: '패키지·버전·용도와 버전 고정 정책.',
    sections: [
      { id: 'summary', heading: '한 줄 요약', guidance: '의존성 관리 방식(중앙 관리 등).' },
      { id: 'packages', heading: '패키지', guidance: '구성 요소별 표(패키지·버전·용도). 매니페스트에서 확인한 버전만.' },
      { id: 'policy', heading: '버전 정책', guidance: '고정·갱신 규칙, lock 파일.' },
      common.evidence, common.caveats, common.related,
    ],
    inputs: (ws) => ({ dependencies: ws.inventory.dependencies, build: ws.inventory.build }),
    readHints: 'Directory.Packages.props, *.csproj, package.json, package-lock.json(버전만).',
  },
  '10_ERROR_HANDLING.md': {
    title: '오류 처리', phase: 'document',
    purpose: '예외·전파·전역 핸들러·오류 응답·재시도·삼켜진 예외.',
    sections: [
      { id: 'summary', heading: '한 줄 요약', guidance: '오류 처리 전략 요약.' },
      { id: 'key-points', heading: '핵심 내용', guidance: '전역 핸들러, 오류 응답 형식, 재시도/타임아웃/롤백 표.' },
      { id: 'details', heading: '상세 내용', guidance: '영역 분석 items를 CONFIRMED_ISSUE/POTENTIAL_RISK/IMPROVEMENT로 나눠 설명. 필요하면 flowchart 1개(id ERR_FLOW).' },
      common.evidence, common.caveats, common.related,
    ],
    inputs: (ws) => ({ errorHandling: opsArea(ws.operations.errorHandling), failurePoints: [...ws.featureAnalyses.values()].flatMap((fa) => fa.failurePoints.slice(0, 6).map((p) => ({ feature: fa.feature.id, ...p }))) }),
    readHints: '미들웨어·ExceptionHandler·ErrorBoundary·Results.Problem 사용처.',
  },
  '13_SECURITY.md': {
    title: '보안', phase: 'document',
    purpose: '인증·인가·비밀·인젝션·업로드·CORS/CSRF/XSS/SSRF·헤더·속도 제한.',
    sections: [
      { id: 'summary', heading: '한 줄 요약', guidance: '신뢰 경계와 핵심 통제.' },
      { id: 'key-points', heading: '핵심 내용', guidance: '통제 표(영역 · 구현 · 코드).' },
      { id: 'details', heading: '상세 내용', guidance: '영역 분석 items를 분류별로. 이미 잘 처리된 것도 적는다.' },
      common.evidence, common.caveats, common.related,
    ],
    inputs: (ws) => ({ security: opsArea(ws.operations.security), network: ws.architecture.runtime.network }),
    readHints: '인증·미들웨어·헤더·정제 파이프라인 코드, Caddyfile.',
  },
  '14_PERFORMANCE.md': {
    title: '성능', phase: 'document',
    purpose: 'N+1·I/O·비동기·락·메모리·캐시·네트워크·직렬화.',
    sections: [
      { id: 'summary', heading: '한 줄 요약', guidance: '성능 특성 요약.' },
      { id: 'key-points', heading: '핵심 내용', guidance: '캐시·제한·풀링 등 이미 있는 장치 표.' },
      { id: 'details', heading: '상세 내용', guidance: '영역 분석 items를 분류별로.' },
      common.evidence, common.caveats, common.related,
    ],
    inputs: (ws) => ({ performance: opsArea(ws.operations.performance) }),
    readHints: '캐시·속도 제한·DB 조회 코드.',
  },
  '15_TESTING.md': {
    title: '테스트', phase: 'document',
    purpose: '테스트 지형·실행·CI·알려진 불안정.',
    sections: [
      { id: 'summary', heading: '한 줄 요약', guidance: '테스트 종류와 수(확인 가능한 범위).' },
      { id: 'key-points', heading: '핵심 내용', guidance: '프로젝트별 테스트 표(위치·프레임워크·실행 명령·외부 자원).' },
      { id: 'details', heading: '상세 내용', guidance: 'CI 파이프라인 단계, 커버리지 갭(techDebt에서), 불안정 테스트.' },
      common.evidence, common.caveats, common.related,
    ],
    inputs: (ws) => ({ tests: ws.inventory.tests, ci: ws.inventory.ci, missingTests: ws.operations.techDebt.items.filter((i) => /테스트|test/i.test(i.title + i.description)) }),
    readHints: '테스트 프로젝트 파일, .github/workflows/*.yml, vitest/playwright 설정.',
  },
  '16_DEPLOYMENT.md': {
    title: '배포', phase: 'document',
    purpose: '이미지·compose·프록시·DB 롤·백업/복원·스모크·운영 확인.',
    sections: [
      { id: 'summary', heading: '한 줄 요약', guidance: '배포 토폴로지 한 줄.' },
      { id: 'key-points', heading: '핵심 내용', guidance: '컨테이너/네트워크/볼륨 표. 필요하면 architecture 다이어그램 1개(id DEPLOY_TOPOLOGY).' },
      { id: 'procedure', heading: '절차', guidance: '배포·백업·복원·스모크 명령(실존 스크립트만).' },
      common.evidence, common.caveats, common.related,
    ],
    inputs: (ws) => ({ containers: ws.inventory.containers, ci: ws.inventory.ci, processes: ws.architecture.runtime.processes, network: ws.architecture.runtime.network, storage: ws.architecture.storage }),
    readHints: 'deploy/ 디렉터리, Dockerfile, Caddyfile, compose, deploy/OPERATIONS.md(INFERRED).',
  },
  '18_GLOSSARY.md': {
    title: '용어집', phase: 'document',
    purpose: '이 프로젝트에서만 쓰는 이름·개념·약어를 코드 이름과 연결한다.',
    sections: [
      { id: 'summary', heading: '한 줄 요약', guidance: '용어 수와 범위.' },
      { id: 'terms', heading: '용어', guidance: '용어 · 뜻 · 코드 이름/경로 · 관련 문서 표(가나다/알파벳 순).' },
      common.related,
    ],
    inputs: (ws) => ({ features: featureIndex(ws), components: componentIndex(ws), entities: ws.data.entities.map((e) => e.name), dtos: ws.data.dtos.map((d) => d.name) }),
    readHints: '필요 시 도메인 클래스.',
  },
};
