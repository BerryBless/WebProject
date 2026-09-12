---
name: architecture-review
description: ".NET/C# 코드의 아키텍처를 심층 감사한다. SOLID 원칙, 레이어 위반, 결합도·응집도, 의존성 방향, 설계 패턴을 체계적으로 분석하고 JSON 결과를 {run_dir}/02_architecture_findings.json에 출력한다. architecture-reviewer 에이전트가 사용하는 전용 스킬."
---

# Architecture Review Skill

## 입력 읽기

1. `{run_dir}/00_input/meta.json`을 읽어 `run_id`, `target_type`, `head_sha`를 확인한다 (`run_dir`은 프롬프트로 전달됨. 없으면 `_workspace/code-review/latest.txt` 참조).
2. `{run_dir}/00_input/diff.txt`를 **처음부터 끝까지** Read로 읽는다. 길면 `offset`/`limit`으로 나눠 읽고, `index.md`가 있으면 파일 위치 탐색에만 쓴다. 요약만으로 판단하지 않는다.
3. diff 형식이면 `+` 줄(추가)과 변경된 파일에 집중하되, 삭제된 추상화(인터페이스·팩토리 제거)도 확인한다. `=== FILE: ... ===` 형식(전체 파일)이면 전체를 분석한다.

## 저장소 문맥 조사 (읽기 전용)

다음 항목은 diff만으로 판정할 수 없다. 필수 보충 파일을 조회하고, 조회할 수 없으면 `unverified`에 기록한다.

| 판정 항목 | 필수 보충 조회 | 조회 불가 시 |
|----------|--------------|------------|
| 프로젝트 참조 방향 | 관련 `*.csproj`의 `<ProjectReference>` (Glob `**/*.csproj`) | `unverified: 프로젝트 참조 방향` |
| 레이어 배치 | 변경 파일의 네임스페이스·폴더와 같은 프로젝트의 다른 클래스 1~2개 | 추정임을 `detail`에 명시 |
| DI 등록 방식 | `Program.cs`/`Startup.cs`의 `services.Add*` | `unverified: DI 등록` |

`target_type=pr`이면 작업 트리 대신 `git show {head_sha}:<경로>`로 읽는다.

## 감사 체크리스트

### 1. SOLID 원칙

| 원칙 | 탐지 패턴 |
|------|----------|
| **SRP** | 클래스가 2가지 이상의 변경 이유를 가짐. 메서드가 데이터 접근 + 비즈니스 로직 + UI 포맷을 동시에 처리. |
| **OCP** | if/switch로 타입을 분기하면서 새 타입 추가 시 기존 클래스를 수정해야 함. 다형성이나 전략 패턴으로 해결 가능한 경우. |
| **LSP** | 오버라이드 메서드가 부모 계약(전제조건/결과조건)을 위반. 파생 클래스가 `NotImplementedException` 던짐. |
| **ISP** | 인터페이스가 구현체가 사용하지 않는 멤버를 강제. 하나의 인터페이스에 10개+ 메서드. |
| **DIP** | 상위 레이어가 구체 타입(`new ConcreteClass()`)을 직접 생성. 인터페이스 없이 하위 레이어에 직접 의존. |

### 2. 레이어 경계 위반

화살표는 **컴파일 의존(프로젝트 참조) 방향**이다. Domain은 아무것도 참조하지 않고, Infrastructure가 Domain의 인터페이스를 구현한다(의존성 역전).

```
Presentation ──▶ Application ──▶ Domain ◀── Infrastructure
                      └──────────────────────────▶ (Infrastructure 는 합성 루트에서만 참조)
```

- **컨트롤러에 비즈니스 로직**: Controller/최소 API 핸들러에 if/계산/도메인 규칙이 있는 경우
- **도메인에 인프라 의존**: Domain 엔티티/서비스가 DbContext, HttpClient, ILogger를 직접 참조
- **Application이 Infrastructure 구체 타입 참조**: 인터페이스 대신 구체 Repository 직접 사용
- **프로젝트 참조 역전**: `.csproj`에서 `Domain → Infrastructure` 또는 `Domain → Application` 참조가 있으면 위반(위 그림의 화살표 반대 방향)

### 3. 결합도·응집도

- **갓 클래스**: 500줄+ 클래스, 10개+ 공개 메서드, 5개+ 서비스 의존성 주입
- **기능 편애**: 메서드가 자기 클래스보다 다른 클래스의 데이터를 더 많이 사용
- **서비스 로케이터 안티패턴**: `IServiceProvider.GetService<T>()` 런타임 해결
- **Shotgun Surgery**: 하나의 개념 변경이 여러 클래스에 흩어진 수정을 요구하는 구조

### 4. 설계 패턴

- **Repository 오용**: Repository가 비즈니스 로직을 포함, 또는 `IQueryable` 반환으로 캡슐화 파괴
- **팩토리 없는 복잡 객체 생성**: 의존성 많은 객체를 곳곳에서 `new`로 생성
- **Observer/Event 누수**: 구독 후 해제 없음, static 이벤트에 인스턴스 구독
- **Mediator 과용**: 단순 CRUD에 MediatR Command/Query 도입으로 오히려 복잡도 증가

## 심각도 기준

| 심각도 | 기준 |
|--------|------|
| **critical** | 전체 아키텍처 붕괴, 레이어 역전, DIP 위반으로 테스트 불가 |
| **high** | 확장성 차단, 의존성 순환, 갓 클래스 (즉시 리팩토링 필요) |
| **medium** | SOLID 부분 위반, 응집도 저하 (다음 스프린트 내 수정 권장) |
| **low** | 패턴 미적용, 구조 개선 가능성 (기술부채 등록 수준) |

## 점수

`score = max(0, 100 − 25×critical − 10×high − 4×medium − 1×low)`. `counts`는 findings에서 센 값과 일치해야 한다.

## 출력

1. 결과를 `{run_dir}/02_architecture_findings.json`(또는 오케스트레이터가 지정한 파일명)에 Write 도구로 저장한다. Write는 이 파일에만 사용한다.
2. 발견사항이 없으면 빈 배열, counts 0, score=100으로 저장한다.
3. 저장 후 **최종 응답 첫 줄**에 `{"status":"done","output":"<경로>","counts":{...},"score":N}`을 적고 종료한다. SendMessage는 사용하지 않는다(팀 도구 없음, 리더 ID 미상).
