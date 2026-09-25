# PortfolioBlog 기술 문서

<!-- doc-harness:section id="summary" hash="45b69568009b4d37954a059e34c22741197824e96f7cdb09f1f99fdd6ea7f524" -->
## 한 줄 요약

단일 작성자용 보안 중심 기술 블로그. 공개 사이트는 JS 없는 서버 렌더링 Razor Pages, 관리 표면은 별도 서브도메인 + IP 허용 목록 + 비밀번호 세션 뒤의 React SPA로 분리된다(README 기준, INFERRED). 백엔드는 ASP.NET Core 10 + EF Core 10 + PostgreSQL 17, 배포는 Docker Compose + Caddy.

이 문서들은 doc-harness가 **실제 코드를 근거로** 생성·검증했다(마지막 갱신 2026-09-25, 문서화 실행 run-0001). 다이어그램은 모두 Mermaid이며 각 문서 안에 원본이 있다. 코드와 문서가 다르면 코드가 맞다 — `문서화`를 다시 실행한다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="docs" hash="16fdd0a401916fe7a5efe5d374d342ad912f8ccee8262d33f2e97846614900bf" -->
## 문서

| 문서 | 내용 |
|---|---|
| [00_EXECUTIVE_SUMMARY.md](00_EXECUTIVE_SUMMARY.md) | 5~10분 안에 프로젝트를 이해하기 위한 요약 |
| [01_PROJECT_OVERVIEW.md](01_PROJECT_OVERVIEW.md) | 목적·기술 스택·구성 |
| [02_ARCHITECTURE.md](02_ARCHITECTURE.md) | 컴포넌트·계층·런타임·제어 흐름 |
| [03_DIRECTORY_STRUCTURE.md](03_DIRECTORY_STRUCTURE.md) | 디렉터리 역할과 파일 트리 |
| [04_SETUP_AND_RUN.md](04_SETUP_AND_RUN.md) | 설치·실행·디버깅 |
| [05_CONFIGURATION.md](05_CONFIGURATION.md) | 설정 키와 환경 |
| [06_DEPENDENCIES.md](06_DEPENDENCIES.md) | 의존성 |
| [07_DATA_MODEL.md](07_DATA_MODEL.md) | 엔티티·관계·ER |
| [08_API.md](08_API.md) | 엔드포인트 표 |
| [09_FEATURES.md](09_FEATURES.md) | 기능 목록(개별 문서는 features/) |
| [10_ERROR_HANDLING.md](10_ERROR_HANDLING.md) | 오류 처리 |
| [11_FAILURE_HISTORY.md](11_FAILURE_HISTORY.md) | 과거 실패와 workaround(누적) |
| [12_TROUBLESHOOTING.md](12_TROUBLESHOOTING.md) | 장애 재발 시 확인 절차 |
| [13_SECURITY.md](13_SECURITY.md) | 보안 |
| [14_PERFORMANCE.md](14_PERFORMANCE.md) | 성능 |
| [15_TESTING.md](15_TESTING.md) | 테스트 |
| [16_DEPLOYMENT.md](16_DEPLOYMENT.md) | 배포 |
| [17_TECH_DEBT.md](17_TECH_DEBT.md) | 기술 부채 |
| [18_GLOSSARY.md](18_GLOSSARY.md) | 용어 |
| [19_UNKNOWN_AND_TODO.md](19_UNKNOWN_AND_TODO.md) | 확인하지 못한 것 |
| [20_CHANGELOG.md](20_CHANGELOG.md) | 의미 있는 구현 변경 기록 |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="features" hash="71a0c0b5cb799f48fec290fe5c55dff9375e9cf58713c1e76e177f24a1f9a3f7" -->
## 기능 문서

| ID | 기능 | 중요도 |
|---|---|---|
| [F001](features/F001_ADMIN_AUTH_SESSION.md) | 관리자 로그인·세션 확인·로그아웃 | CORE |
| [F002](features/F002_ADMIN_POST_LIST.md) | 관리 글 목록 조회·삭제 | CORE |
| [F003](features/F003_ADMIN_POST_EDITOR.md) | 글 작성·수정(마크다운 에디터) | CORE |
| [F004](features/F004_MARKDOWN_PREVIEW.md) | 마크다운 실시간 미리보기 | SUPPORTING |
| [F005](features/F005_LOCAL_DRAFT_RECOVERY.md) | 편집 임시본 자동 보관·복원 | SUPPORTING |
| [F006](features/F006_POST_EDIT_CONFLICT.md) | 글 저장 충돌 감지·비교 해결 | SUPPORTING |
| [F007](features/F007_ADMIN_SERIES_MANAGEMENT.md) | 시리즈 관리 | SUPPORTING |
| [F008](features/F008_ADMIN_TAG_MANAGEMENT.md) | 태그 관리 | SUPPORTING |
| [F009](features/F009_ADMIN_ATTACHMENT_MANAGEMENT.md) | 첨부 이미지 업로드·목록·삭제 | CORE |
| [F010](features/F010_PUBLIC_ATTACHMENT_SERVING.md) | 첨부 이미지 공개 제공 | CORE |
| [F011](features/F011_MARKDOWN_RENDERING_CACHE.md) | 마크다운 렌더링·렌더 결과 캐시 | CORE |
| [F012](features/F012_PUBLIC_HOME_LIST.md) | 공개 홈(최신 글 목록) | CORE |
| [F013](features/F013_PUBLIC_POST_DETAIL.md) | 공개 글 상세 보기 | CORE |
| [F014](features/F014_PUBLIC_SEARCH.md) | 공개 글 검색 | SUPPORTING |
| [F015](features/F015_PUBLIC_SERIES_PAGE.md) | 공개 시리즈별 글 목록 | SUPPORTING |
| [F016](features/F016_PUBLIC_TAG_PAGE.md) | 공개 태그별 글 목록 | SUPPORTING |
| [F017](features/F017_PUBLIC_SITE_FEEDS.md) | 피드·사이트맵·robots·코드 강조 CSS 제공 | SUPPORTING |
| [F018](features/F018_ADMIN_SURFACE_ACCESS_CONTROL.md) | 관리 표면 접근 통제(호스트·IP 허용 목록·CSRF 헤더·Origin) | INFRA |
| [F019](features/F019_RATE_LIMITING.md) | 요청 속도 제한 | INFRA |
| [F020](features/F020_WEB_PIPELINE_PROTECTION.md) | 보안 헤더·오류 응답·과부하 처리·API 본문 제한 | INFRA |
| [F021](features/F021_STARTUP_BOOTSTRAP.md) | 앱 기동 부트스트랩(설정 검증·마이그레이션·공개 DB 롤 권한) | INFRA |
| [F022](features/F022_HEALTH_CHECK.md) | 헬스체크 | INFRA |
| [F023](features/F023_HASH_PASSWORD_CLI.md) | 관리자 비밀번호 해시 생성 CLI | INFRA |
| [F024](features/F024_ATTACHMENT_JANITOR.md) | 고아 첨부 파일 정리(백그라운드) | SUPPORTING |
| [F025](features/F025_EDGE_PROXY_TLS.md) | 에지 프록시·TLS·관리 SPA 정적 서빙 | INFRA |
| [F026](features/F026_COMPOSE_DEPLOYMENT.md) | Docker Compose 운영 배포 구성 | INFRA |
| [F027](features/F027_BACKUP_RESTORE.md) | DB·첨부 백업과 복원 | INFRA |
| [F028](features/F028_DEPLOY_SMOKE_TEST.md) | 배포 스모크 검증 | INFRA |
| [F029](features/F029_ADMIN_SPA_SHELL.md) | 관리 SPA 셸(라우팅·API 클라이언트·오류/없는 화면) | SUPPORTING |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="legend" hash="674cb4dc20b22b9710ae2e86de6160b2e16614326d513760c5620b953dc7d966" -->
## 상태 표기

| 상태 | 뜻 |
|---|---|
| CONFIRMED | 코드에서 직접 확인 |
| INFERRED | 정황·문서·이력에서 추론 |
| UNKNOWN | 확인하지 못함 |
| POSSIBLE_LEGACY | 더 이상 쓰이지 않는 것으로 보임 |
| POTENTIAL_ISSUE | 문제 가능성 관찰 |
<!-- /doc-harness:section -->
