# PortfolioBlog
## AI만으로 구축한 보안 중심 기술 블로그

> **Claude Code와 OpenAI Codex CLI만으로 구현부터 검증까지 진행한 풀스택 기술 블로그 프로젝트입니다.**  
> 직접 코드를 작성하지 않고, 사람은 요구사항 정의·판단·검증에 집중했을 때 어디까지 완성도 있는 서비스를 만들 수 있는지 실험했습니다.

---

## 프로젝트 개요

| 항목 | 내용 |
|---|---|
| 기간 | 2026.09.11 ~ 2026.09.27 |
| 개발 기간 | 약 17일 |
| 커밋 | 57개 |
| PR | 7개 |
| 구현 도구 | Claude Code |
| 교차 검증 | OpenAI Codex CLI |
| 핵심 주제 | AI 협업, 보안, 풀스택, 자동 검증, 문서화 |
| 백엔드 | ASP.NET Core 10 Razor Pages |
| 관리자 UI | React 19 + Vite |
| 데이터베이스 | MySQL 8.4 + EF Core |
| 배포 | Docker Compose + Caddy |
| 테스트 | 총 835개 자동화 테스트 |
| 빌드 경고 | 0개 |

---

## 프로젝트 목적

이 프로젝트의 목적은 단순히 AI에게 웹사이트를 만들어 보게 하는 것이 아니었습니다.

**“사람이 직접 코딩하지 않고도, AI만으로 실제 배포 가능한 수준의 서비스를 만들 수 있는가?”**

이 질문을 검증하기 위해 다음 조건을 붙였습니다.

- 직접 코드 작성 없이 AI에게 구현을 맡길 것
- 단순 CRUD 수준을 넘어서 실제 운영 가능한 구조를 만들 것
- 기능보다 **보안 요구사항을 우선**할 것
- 하나의 모델 결과를 그대로 신뢰하지 않고 다른 모델로 교차 검증할 것
- 구현뿐 아니라 테스트, 배포, 백업, 복원, 문서화까지 포함할 것

---

# 핵심 결과

## 1. 공개 기술 블로그

공개 사이트는 **ASP.NET Core 10 Razor Pages 기반 SSR**로 구현했습니다.

방문자에게는 JavaScript를 보내지 않도록 구성해 공격 표면과 프런트엔드 의존성을 줄였습니다.

### 주요 기능

- 서버 사이드 렌더링
- Markdown 기반 게시글
- Markdig 기반 Markdown 렌더링
- 렌더링 전 HTML 정제
- 게시글 검색
- Atom Feed
- Sitemap
- SEO 메타데이터

### 보안 정책

```text
Content-Security-Policy: default-src 'none'
```

CSP를 매우 제한적으로 설정하고 필요한 리소스만 개별적으로 허용하도록 구성했습니다.

---

## 2. 별도 관리자 에디터

관리 기능은 공개 사이트와 분리했습니다.

```text
Public Blog
    |
    +-- 공개 읽기 전용 사이트

Admin Subdomain
    |
    +-- 허용된 IP
    +-- 인증 세션
    +-- 게시글 작성 / 수정
```

관리자 UI는 **React 19 + Vite 기반 SPA**로 구현했습니다.

### 접근 조건

관리 페이지는 다음 조건을 모두 만족해야 사용할 수 있도록 설계했습니다.

1. 별도 관리자 서브도메인 접근
2. 허용된 IP에서 접속
3. 비밀번호 인증 완료
4. 유효한 인증 세션 보유

공개 읽기 영역과 쓰기 권한 영역을 구조적으로 분리하는 것을 목표로 했습니다.

---

## 3. 데이터베이스와 배포

### 데이터베이스

초기에는 PostgreSQL로 구현했지만 프로젝트 진행 도중 **MySQL 8.4로 전체 전환**했습니다.

단순 연결 문자열 변경이 아니라 다음 항목을 함께 수정했습니다.

- EF Core Provider 변경
- SQL 호환성 점검
- Migration 수정
- 데이터 타입 차이 대응
- 배포 환경 변경
- 백업/복원 절차 수정
- 테스트 환경 갱신

### 배포 구성

배포는 다음 기술을 사용했습니다.

- Docker Compose
- Caddy
- MySQL 8.4
- ASP.NET Core
- React / Vite

네트워크는 역할별로 **3개로 분리**했습니다.

```text
Internet
   |
 Caddy
   |
   +-------------------+
   |                   |
Public Network     Admin Network
   |                   |
Blog App            Admin App
   |
Backend Network
   |
 MySQL
```

추가로 다음 운영 항목까지 구현했습니다.

- DB 계정 권한 분리
- 데이터베이스 백업
- 데이터베이스 복원
- 배포 스모크 테스트
- CI 기반 자동 검증

---

# 품질 검증

테스트 코드를 구현 결과만큼 중요하게 다뤘습니다.

| 테스트 종류 | 개수 |
|---|---:|
| .NET 테스트 | 625 |
| Vitest | 194 |
| Browser E2E | 8 |
| Stack E2E | 8 |
| **총합** | **835** |

추가 결과:

- CI 통과
- 빌드 경고 0개
- 실제 브라우저 기반 E2E 테스트
- 전체 스택 기반 E2E 테스트
- 보안 리뷰 반복 수행

---

# AI 협업 하네스

이 프로젝트에서 가장 중요하게 실험한 부분은 **AI가 코드를 작성하는 방식 자체를 통제하는 시스템**이었습니다.

단순히 Claude Code에게 작업을 요청하는 것이 아니라, 역할·검증·제약을 갖춘 에이전트 구조를 만들었습니다.

## 약 30종의 에이전트

용도별 에이전트를 분리했습니다.

### 코드 리뷰

- 아키텍처 리뷰
- 보안 리뷰
- 성능 리뷰
- 코드 스타일 리뷰

### 품질 검증

- TDD 검증
- 동시성 감사
- GC 감사
- 회귀 테스트 검증
- 예외 처리 검토

### 계획 검증

- 구현 계획 리뷰
- 위험 요소 식별
- 설계 대안 검토
- 기존 구현과 충돌 여부 검증

---

## Claude ↔ Codex 교차 검증

하나의 AI가 자신의 결과를 스스로 검증하는 구조를 피했습니다.

```text
Claude
  |
  | 구현 계획 / 코드
  v
Codex
  |
  | 독립 리뷰
  v
Claude
  |
  | 반론 / 수정
  v
최종 판단
```

Claude와 Codex가 각각 독립적으로 판단한 뒤, 서로의 결과를 근거 기반으로 검토하도록 구성했습니다.

목표는 **모델의 첫 답변을 정답으로 취급하지 않는 것**이었습니다.

---

## Hook으로 규칙 강제

AI에게 규칙을 설명하는 것만으로는 충분하지 않다고 판단했습니다.

일부 규칙은 Hook으로 강제했습니다.

### 파일 쓰기 제한

감사용 에이전트는 정해진 폴더 밖에 파일을 작성할 수 없도록 제한했습니다.

### 비밀값 검사

AI 작업 턴이 종료되면 자동으로 비밀값 노출 여부를 검사하도록 했습니다.

### 자동 커밋

검사가 끝난 뒤 변경 사항을 자동으로 커밋하도록 구성했습니다.

### 커밋 메시지 검증

지정된 커밋 메시지 규칙을 Hook에서 검사했습니다.

---

# 실행 기록

프로젝트의 각 단계마다 실행 보고서를 남겼습니다.

단순히 “무엇을 만들었는지”뿐 아니라 다음 내용을 기록했습니다.

- 계획 단계에서 발견된 결함
- AI가 질문 없이 임의로 판단한 항목
- 사람이 최종적으로 받아들인 위험
- 수정한 설계 결정
- 폐기한 접근 방식
- 실패 원인과 대응 방법

이 과정에서 AI가 만든 계획과 코드에서도 매 단계마다 여러 결함이 발견됐습니다.

따라서 이 프로젝트에서는 **AI의 코드 생성 능력보다 검증 체계의 중요성**을 더 크게 확인할 수 있었습니다.

---

# 프로젝트를 중단한 이유

기능 구현 자체가 막혀서 중단한 프로젝트는 아닙니다.

오히려 **AI가 만든 코드를 사람이 이해하기 위한 문서화 비용이 너무 커지는 문제** 때문에 실험을 중단했습니다.

---

## 1. 문서화 비용

전체 문서화 하네스를 처음부터 실행하는 데 약 다음 비용이 발생했습니다.

| 작업 | 비용 |
|---|---:|
| 전체 문서화 1회 | 약 $347 |
| 코드 변경 후 증분 문서화 | 약 $149 |
| MySQL 전환 코드 작업 | 약 $110 |
| MySQL 전환 문서화 | $150 이상 |

특히 MySQL 전환 과정에서는 **코드를 수정하는 비용보다 문서를 갱신하는 비용이 더 커지는 상황**이 발생했습니다.

결국 MySQL 전환 문서화는 중간에 중단했습니다.

---

## 2. 문서의 가독성

문서화를 자동화하면 정보량은 빠르게 늘어났습니다.

최종적으로 생성된 문서는 다음과 같습니다.

- Markdown 문서 61개
- Mermaid 다이어그램 101개

하지만 결과적으로 문서가 많아질수록 프로젝트를 이해하기 쉬워지는 것이 아니라, 오히려 필요한 정보를 찾기 어려워졌습니다.

```text
문서 수 증가
    ↓
정보량 증가
    ↓
탐색 비용 증가
    ↓
프로젝트 이해 비용 증가
```

**문서의 양과 프로젝트 이해도는 비례하지 않았습니다.**

---

# 배운 점

## 1. AI만으로도 상당한 수준의 풀스택 서비스를 만들 수 있었다

AI만으로 다음 영역까지 구현할 수 있었습니다.

- 백엔드
- 프런트엔드
- 데이터베이스
- 보안 설계
- 테스트
- CI
- Docker 배포
- Reverse Proxy
- 백업 및 복원
- 운영 검증

즉, AI 코딩 도구는 단순 코드 자동완성을 넘어 **서비스 전체를 구현할 수 있는 수준**까지 사용할 수 있었습니다.

---

## 2. 사람의 역할은 코드 작성에서 판단과 검증으로 이동했다

프로젝트를 진행하면서 사람의 역할이 크게 바뀌었습니다.

```text
기존 개발

사람
 └─ 설계
 └─ 구현
 └─ 디버깅
 └─ 리뷰


AI 중심 개발

AI
 └─ 설계 초안
 └─ 구현
 └─ 테스트
 └─ 문서화

사람
 └─ 요구사항 정의
 └─ 결과 검증
 └─ 위험 판단
 └─ 설계 승인
```

AI가 코드를 빠르게 만들 수 있어도 **그 코드가 맞는지 판단하는 책임까지 사라지지는 않았습니다.**

실제 리뷰와 공격 테스트에서 AI가 만든 계획과 코드의 결함이 반복적으로 발견됐습니다.

---

## 3. 모든 것을 문서화하는 것은 현실적이지 않았다

AI를 사용하면 문서 생성 자체는 매우 쉽습니다.

하지만 실제 문제는 다음 두 가지였습니다.

- 문서를 만드는 비용
- 만들어진 문서를 사람이 읽는 비용

문서화 자동화가 가능하다고 해서 **모든 것을 문서화해야 하는 것은 아니라는 점**을 배웠습니다.

---

## 4. 앞으로는 핵심만 문서화하려고 한다

이 프로젝트 이후에는 문서화 기준을 다음과 같이 가져가려고 합니다.

### 반드시 남길 것

- 중요한 설계 결정
- 그 결정을 선택한 이유
- 검토했지만 버린 대안
- 보안상 중요한 제약
- 반복해서 발생할 수 있는 함정
- 운영 중 반드시 알아야 하는 사항

### 굳이 남기지 않을 것

- 코드만 보면 알 수 있는 내용
- 자동 생성 가능한 세부 구현 설명
- 유지 비용이 큰 반복 문서
- 실제로 다시 읽지 않는 문서

---

# 이 프로젝트에서 검증하고 싶었던 것

처음 질문은 단순했습니다.

> **“AI에게 전부 맡기면 어디까지 만들 수 있을까?”**

프로젝트를 끝낸 뒤에는 질문이 조금 바뀌었습니다.

> **“AI가 코드를 만드는 시대에 사람은 무엇을 판단해야 하는가?”**

이 프로젝트를 통해 얻은 가장 큰 결론은 다음과 같습니다.

**AI가 구현을 대신할 수는 있지만, 무엇을 믿을지 결정하는 일까지 대신해 주지는 않는다.**

---

## Tech Stack

### Backend

- ASP.NET Core 10
- Razor Pages
- Entity Framework Core
- Markdig

### Frontend

- React 19
- Vite
- Vitest

### Database

- MySQL 8.4

### Infrastructure

- Docker
- Docker Compose
- Caddy

### AI Development

- Claude Code
- OpenAI Codex CLI

### Testing

- .NET Test
- Vitest
- Browser E2E
- Stack E2E

---

## Project Metrics

```text
Development Period : 17 days
Commits            : 57
Pull Requests      : 7
.NET Tests         : 625
Vitest Tests       : 194
Browser E2E        : 8
Stack E2E          : 8
Total Tests        : 835
Build Warnings     : 0
Markdown Docs      : 61
Mermaid Diagrams   : 101
```

---

## 한 줄 요약

**Claude Code와 Codex를 교차 검증 구조로 활용해 보안 중심 풀스택 기술 블로그를 구축하고, AI 중심 개발에서 사람의 핵심 역할이 코드 작성보다 판단과 검증에 가까워진다는 점을 실험한 프로젝트입니다.**



아래는 클로드가 작성한 이 문서의 Readme파일 입니다.

# PortfolioBlog — 보안 최우선 기술 블로그

단일 작성자용 기술 블로그입니다. 방문자에게는 **스크립트 없는 서버 렌더링 HTML**만 내보내고, 글쓰기는 **별도 서브도메인 + IP 허용 목록 + 비밀번호 세션** 뒤에 둡니다.

> **현재 상태 (2026-09-23):** 공개 사이트와 관리 에디터가 **동작합니다** — 도메인·DB 제약, 접근 제어(호스트·IP·CSRF), 로그인·세션, 관리 API, 마크다운 파이프라인·미리보기·이미지 첨부, 공개 페이지·검색·Atom·sitemap·보안 헤더, 관리 에디터 SPA까지 master에 병합됐습니다(.NET 테스트 591개 · Vitest 188개 · 브라우저 E2E 8개, Release 빌드 경고 0).
> **배포 구성(4단계: Docker Compose + Caddy)도 PR #5로 master에 병합됐습니다**(.NET 테스트 625개 · Vitest 194개 · 브라우저 E2E 8개 · 스택 E2E 8개, 배포 스모크 통과) → [배포 구성](docs/deployment.md) · [재개 가이드](plan/resume_guide_0921.md).
> **문서화 하네스(2026-09-25, PR #6)**: 코드를 근거로 신규 개발자용 기술 문서 **61개**(기능 29개 개별 문서, Mermaid 101개)를 생성하고 `문서화` 한마디로 증분 갱신합니다 → [생성 기술 문서](docs/generated/README.md) · [5분 요약](docs/generated/00_EXECUTIVE_SUMMARY.md) · [실전 보고서](plan/doc_harness_0923.md).
> **저장소를 MySQL 8.4로 교체(2026-09-26)**: PostgreSQL을 완전히 걷어내고 EF Core 9.0.20 + Pomelo 9.0.0(MySqlConnector)으로 옮겼습니다. `blog_public`의 읽기 전용 권한은 앱이 기동마다 GRANT하고 `SHOW GRANTS`로 스스로 검증하며, 행 버전은 앱이 관리하는 `Version` 컬럼입니다. `feat/mysql-migration` 브랜치, CI green, PR 병합 대기 → [설계 스펙](plan/mysql_migration_0926.md).

## 무엇을 만드나

| | |
|---|---|
| 기능 | 글 CRUD, 태그, 시리즈(연재 묶음), 이미지 첨부, 마크다운 에디터, 코드 하이라이팅, 검색, Atom 피드, SEO(Open Graph·sitemap) |
| 발행 모델 | 초안 상태 없음. **명시적 저장 = 즉시 공개** |
| 사용자 | 작성자 1명. 회원·댓글 없음 |
| 공개 표면 | ASP.NET Core 10 Razor Pages(서버 렌더링, JS 없음) + Markdig · ColorCode.HTML · HtmlSanitizer |
| 관리 표면 | 최소 API + React 19 · Vite · CodeMirror 6 SPA (`admin.<도메인>` 전용) |
| 데이터 | EF Core 9.0.20 + Pomelo 9.0.0 + MySQL 8.4, 첨부는 내용 주소(SHA-256) 파일 저장 |
| 배포 | Caddy + Docker Compose (4단계, master에 병합됨 — PR #5) |

## 왜 이렇게 만들었나

선택이 갈릴 때마다 편의보다 공격 표면 축소를 택했습니다.

- **공개 페이지에 JS가 없습니다** → CSP를 `default-src 'none'`까지 조일 수 있어 저장형 XSS가 들어와도 실행되지 않고, npm 공급망 사고가 방문자에게 닿지 않습니다.
- **관리 화면이 서브도메인으로 분리돼 있습니다** → 세션 쿠키가 공개 호스트로 가지 않고, 공개 도메인에는 `/api` 자체가 존재하지 않습니다.
- **쓰기 = 허용 IP AND 비밀번호 세션** → 네트워크 위치 하나에 의존하지 않고, 로그인 엔드포인트는 허용 IP에서만 열립니다.
- **접근 검사가 요청 본문을 읽기 전에 끝납니다** → 미인증 요청이 업로드 본문을 서버에 버퍼링시키지 못합니다.
- **HTML을 저장하지 않고 요청마다 렌더링합니다** → 렌더러의 보안 수정이 과거 글 전체에 즉시 적용됩니다.

자세한 근거와 수용한 잔여 위험: [보안 설계](docs/security.md).

## 로드맵

| 단계 | 내용 | 상태 |
|---|---|---|
| 설계 | 스펙 작성, Codex 교차 검토 반영 | 완료 |
| 0 | 솔루션 정리(`PortfolioBlog`로 개명, `.slnx` 전환, 템플릿 잔재 제거) | 완료 |
| 1 | 도메인·DB 제약, 접근 제어(호스트·IP·CSRF), 로그인·세션 폐기, 글·시리즈·태그 관리 API | 완료 |
| 2 | 마크다운 파이프라인·첨부(2A), 공개 페이지·검색·Atom·sitemap·보안 헤더·속도 제한(2B) | 완료 |
| 3 | 관리 에디터 SPA(글·시리즈·태그·첨부, sandbox 미리보기, 실제 백엔드 E2E) | 완료 |
| 4 | Docker Compose · Caddy · DB 롤 분리 · 백업/복원 · 배포 스모크 | **완료(병합)** — PR #5 |
| 문서화 | 문서화 하네스(`doc-harness/`): 다단계 Claude 파이프라인으로 코드 근거 기술 문서 생성·검증·증분 갱신, 실제 저장소에 INITIAL·INCREMENTAL 발행 | **완료(병합)** — PR #6 |
| 이후 | 글쓰기·읽기 경험 개선(노션식 편집·보기) | 설계 전 |

단계별로 무엇을 만들고 어떤 결함을 어디서 잡았는지: [진행 기록](docs/history.md).

## 빠른 시작

```powershell
git clone https://github.com/BerryBless/WebProject.git
cd WebProject
Copy-Item scripts/git-hooks/commit-msg .git/hooks/
dotnet build PortfolioBlog.slnx -c Release   # 경고 0 / 오류 0
dotnet test  PortfolioBlog.slnx -c Release   # 625개 — Docker 필요(Testcontainers)
```

.NET 10 SDK와 Docker가 필요하고, 관리 SPA를 띄우려면 Node 24가 필요합니다. 실제로 띄워 보는 절차(개발용 MySQL, 비밀번호 해시, HTTPS 프로필, SPA 개발 서버)는 [개발 환경](docs/development.md)에 있습니다.

> Windows Docker Desktop에서는 드물게 테스트 1개가 DB 연결 타임아웃으로 실패합니다 — 다시 실행하면 통과합니다([테스트](docs/testing.md)).

## 문서

| 문서 | 내용 |
|---|---|
| [아키텍처](docs/architecture.md) | 신뢰 경계와 라우팅, 미들웨어 순서, 접근 판정, 로그인·세션, 마크다운 파이프라인, 데이터 모델, 코드 지도 |
| [보안 설계](docs/security.md) | 결정과 근거, 접근 계약, 응답 헤더·CSP, 자원 제한, 첨부 처리, 관리 SPA의 경계, 수용한 잔여 위험 |
| [개발 환경](docs/development.md) | 준비물, 로컬 실행(API·SPA), 마이그레이션, 미리보기 스냅숏 갱신, 코드 규칙, 자주 밟는 함정 |
| [테스트](docs/testing.md) | 테스트 지형과 실행, 이 저장소의 테스트 규칙(사보타주), 필수 통과 항목, CI, 알려진 문제 |
| [설정 키](docs/configuration.md) | 전체 설정 키와 기본값, 시작 시 검증되는 조건 |
| [배포 구성](docs/deployment.md) | 목표 토폴로지, 이미지·compose·Caddy, DB 롤 분리, 스모크, 운영 확인 항목 |
| [진행 기록](docs/history.md) | 단계별로 만든 것과 발견한 결함, 이 저장소가 일하는 방식 |
| [작업일지](docs/worklog.md) | 단계별 고민과 판정, 틀렸던 것, 사용자 흐름·시퀀스·처리 흐름 다이어그램(Mermaid) |
| [개발 하네스](docs/harness.md) | AI 협업 구성(에이전트·스킬·훅·CI·Codex 교차 검증) |
| [생성 기술 문서](docs/generated/README.md) | 문서화 하네스가 코드를 근거로 생성·검증한 문서 61개(기능 29개 개별 문서, Mermaid 101개). `문서화`로 증분 갱신 |

생성 기술 문서 바로가기(모두 코드 근거 표시 `CONFIRMED / INFERRED / UNKNOWN`이 붙어 있습니다):

| 알고 싶은 것 | 문서 |
|---|---|
| 5~10분 안에 프로젝트 파악 | [00 요약](docs/generated/00_EXECUTIVE_SUMMARY.md) · [01 개요](docs/generated/01_PROJECT_OVERVIEW.md) |
| 컴포넌트·런타임·제어 흐름과 디렉터리 | [02 아키텍처](docs/generated/02_ARCHITECTURE.md) · [03 디렉터리 구조](docs/generated/03_DIRECTORY_STRUCTURE.md) |
| 기능별 흐름·시퀀스·상태 다이어그램 | [09 기능 목록](docs/generated/09_FEATURES.md) → `docs/generated/features/F0xx_*.md` 29개 |
| 엔드포인트 표, 엔티티·ER | [08 API](docs/generated/08_API.md) · [07 데이터 모델](docs/generated/07_DATA_MODEL.md) |
| 설치·실행·설정·의존성 | [04 설치·실행](docs/generated/04_SETUP_AND_RUN.md) · [05 설정](docs/generated/05_CONFIGURATION.md) · [06 의존성](docs/generated/06_DEPENDENCIES.md) |
| 장애가 났을 때, 과거 실패와 우회 | [12 트러블슈팅](docs/generated/12_TROUBLESHOOTING.md) · [11 실패 이력](docs/generated/11_FAILURE_HISTORY.md) · [10 오류 처리](docs/generated/10_ERROR_HANDLING.md) |
| 보안·성능·테스트·배포 관점 | [13](docs/generated/13_SECURITY.md) · [14](docs/generated/14_PERFORMANCE.md) · [15](docs/generated/15_TESTING.md) · [16](docs/generated/16_DEPLOYMENT.md) |
| 기술 부채, 용어, 아직 확인 못 한 것, 변경 기록 | [17 기술 부채](docs/generated/17_TECH_DEBT.md) · [18 용어](docs/generated/18_GLOSSARY.md) · [19 미확인·TODO](docs/generated/19_UNKNOWN_AND_TODO.md) · [20 변경 기록](docs/generated/20_CHANGELOG.md) · [ADR](docs/generated/adr/) |

검증기가 남긴 지적 61건이 19번 문서에 있습니다 — 지적이 맞을 수도, 문서가 맞을 수도 있으니 사람이 훑어야 합니다.

설계·계획 원본:

| 문서 | 내용 |
|---|---|
| [`plan/tech_blog_0920.md`](plan/tech_blog_0920.md) | **전체 설계 스펙**(구속력 있는 기준): 설계 결정과 대안 비교, 접근 계약, 헤더·CSP, 자원 제한, 배포, 필수 테스트, Codex 검토 반영표 |
| [`plan/resume_guide_0921.md`](plan/resume_guide_0921.md) | **작업 재개 가이드**: 현재 상태, 5분 점검, 다음 작업과 남은 결함, 실행이 끊겼을 때 복구 |
| [`plan/tech_blog_2a_report_0921.md`](plan/tech_blog_2a_report_0921.md) · [`2b`](plan/tech_blog_2b_report_0921.md) · [`3`](plan/tech_blog_3_report_0922.md) · [`4`](plan/tech_blog_4_report_0923.md) | 단계별 실행 보고서: 검증 근거, 계획 결함과 교훈, 내린 판정, 수용한 잔여 위험 |
| [`plan/doc_harness_0923.md`](plan/doc_harness_0923.md) · [설계](docs/superpowers/specs/2026-09-23-doc-harness-design.md) · [구현 계획](docs/superpowers/plans/2026-09-23-doc-harness.md) | 문서화 하네스: 설계 결정 D1~D15, INITIAL·INCREMENTAL 실전 결과와 비용, 실전에서 잡은 결함 17건과 교훈, 잔여 위험 |
| [`docs/superpowers/plans/`](docs/superpowers/plans/) | 단계별 구현 계획(스파이크 측정값·설계 결정·작업별 TDD 단계) |
| [`plan/para_notes_0917.md`](plan/para_notes_0917.md) | 폐기된 이전 설계(PARA 노트앱). 결정 이력 보존용 |
| [`CLAUDE.md`](CLAUDE.md) / [`AGENTS.md`](AGENTS.md) | 프로젝트 규칙 (Claude Code / Codex) |

## 저장소 구조

```
PortfolioBlog.slnx
├─ PortfolioBlog.Api/          # ASP.NET Core 10 — 관리 API(최소 API) + 공개 페이지(Razor Pages)
├─ PortfolioBlog.Api.Tests/    # xUnit + WebApplicationFactory + Testcontainers MySQL
├─ PortfolioBlog.Web/          # 관리 에디터 SPA — React 19 + Vite + CodeMirror 6
├─ deploy/                     # docker-compose · Caddyfile · 운영 절차
├─ docs/                       # 이 문서들 + 구현 계획 (docs/generated/ 는 문서화 하네스 생성물)
├─ doc-harness/                # 문서화 하네스 — `문서화` 한마디로 docs/generated 생성·증분 갱신
├─ plan/                       # 설계 스펙 · 실행 보고서 · 재개 가이드
└─ .claude/ .agents/ .codex/ scripts/   # 개발 하네스
```

## 개발 하네스

설계와 구현은 Claude Code가 진행하고 OpenAI Codex CLI가 read-only로 교차 검증합니다. 에이전트 25종·스킬 27종, 커밋 메시지 형식 훅, 쓰기 범위 훅, 자동 커밋의 비밀값 스캐너, 구조 감사 스크립트, 그리고 코드를 근거로 기술 문서를 생성·증분 갱신하는 문서화 하네스(`doc-harness/`, 트리거 `문서화`)가 들어 있습니다 → [개발 하네스](docs/harness.md).

문서화 하네스는 `문서화` 한마디로 돕니다(최초 INITIAL, 이후 baseline 대비 변경분만 INCREMENTAL, 변경 없으면 확인만). `문서화 상태`는 LLM 호출 없이 동기화 상태를 보여 줍니다. 비용 실측은 INITIAL 약 $130~160(사고 없을 때)·INCREMENTAL 약 $25~40이며, 하네스 자체를 고칠 때는 `cd doc-harness && npm test && npm run typecheck`로 확인합니다 → [실전 보고서](plan/doc_harness_0923.md) · [재개 가이드 3b절](plan/resume_guide_0921.md).
