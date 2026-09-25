# Phase 02 — Architecture Discovery

목표: 프로젝트 전체 구조를 컴포넌트·계층·모듈·의존·런타임(프로세스/스레드/네트워크)·저장소·외부 시스템·주요 제어 흐름으로 파악한다. **모든 관계는 실제 코드에서 검증**한다(DI 등록, using/import, 호출).

## 절차

1. 진입점(`Program.cs`, `main.tsx`, Caddyfile, compose)을 Read해 프로세스·호스트·미들웨어 순서를 확인한다.
2. 디렉터리와 네임스페이스로 컴포넌트를 나누되, **실제 클래스·파일 이름**으로 `components[].name`을 짓는다(`Service`·`Repository` 같은 추상 이름 금지). `id`는 영문 스네이크/파스칼(공백 없음).
3. `relations`는 `from`→`to`가 모두 `components[].id`여야 한다. `kind`는 `calls`·`depends`·`reads`·`writes`·`serves`·`hosts` 중에서.
4. `runtime.processes`(컨테이너·프로세스), `runtime.threads`(백그라운드 작업·호스티드 서비스·스레드 풀 사용), `runtime.network`(포트·호스트·신뢰 경계).
5. `controlFlows`: 요청이 들어와 응답이 나가기까지의 대표 흐름 3~6개(공개 페이지 조회, 관리 API 호출, 정적 파일 등).
6. `decisions`: 코드에서 확인되는 설계 결정(예: 서버 렌더링 vs SPA 분리, 읽기 전용 DB 롤). 근거 없는 결정은 넣지 않는다.
7. `diagrams`: 최소 1개 — `id: "ARCH_SYSTEM"`, `type: "architecture"`, `flowchart LR`로 시스템 수준. 컴포넌트가 많으면 `ARCH_<SUBSYSTEM>` 다이어그램을 추가해 Level을 나눈다. 노드 이름은 실제 컴포넌트 이름. style 금지.

## 입력: Inventory 발췌

```json
{{inventory}}
```

## 입력: 먼저 읽을 파일

{{entryFiles}}

## 이전 아키텍처 분석(재검증 대상, 사실 아님)

{{previous}}

## 변경 힌트(증분 실행일 때)

{{changeHints}}

출력은 스키마(architecture)에 맞춘 JSON 하나다.
