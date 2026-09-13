# 20. 구현 노트 — `GET /health` 엔드포인트 (최초 구현)

- run: `20260913_070000_health-endpoint`
- base_sha: `524f5a730867ce59e226bd4f9332961b8b8df6f8`
- 구현 근거 문서: `13_final_plan_r2.md` (라운드 2 확정 통합 계획) + 양측 최종 재검토(APPROVE)의 비차단 정확성 지침 4건
- 시도 횟수: **1** (빌드·테스트 1회 통과, 재시도 없음)
- 커밋: **하지 않음.** `.git/` 아래 어떤 파일도 생성하지 않았다(`auto_commit_msg.txt`·`harness_commit_in_progress` 포함).
  - 관측: `.git/harness_commit_in_progress` 센티널이 디스크에 **이미 존재**한다. mtime 은 `2026-09-13 15:12:36` 으로 이 구현 세션의 첫 명령(베이스라인 빌드, 15:5x)보다 이르므로 오케스트레이터(cross-verify 파이프라인)가 만든 것이며, 구현자는 생성·수정·삭제하지 않았다.

## 1. 변경 파일 (제품·테스트 정확히 2개)

| # | 파일 | 종류 | 내용 | numstat |
|---|---|---|---|---|
| 1 | `WebProject.Api/Program.cs` | 수정 | `.WithName("GetWeatherForecast");` 뒤·`app.Run();` 앞에 `/health` `MapGet` 블록 삽입, 파일 하단 `WeatherForecast` record 뒤·`public partial class Program` 앞에 `HealthResponse` record 추가 | `20 0` (삽입 20 / 삭제 0) |
| 2 | `WebProject.Api.Tests/HealthEndpointTests.cs` | 신규 | `IClassFixture<WebApplicationFactory<Program>>` 통합 테스트 2건 | `126 0` (신규 126줄) |

그 외 산출물: 이 run 디렉터리의 `20_impl_notes.md`·`20_test_results.txt` 뿐. csproj·sln·appsettings·기존 테스트 3파일·`WebProject.Sample`·CI·`plan/` 무변경, 패키지 추가 없음.

종료 시점 `git status --short --untracked-files=all -- . ':(exclude)_workspace'` 결과가 정확히 `M WebProject.Api/Program.cs` + `?? WebProject.Api.Tests/HealthEndpointTests.cs` 2줄임을 확인했다(계획 §7.7 / P-X10 / r2 X-F4). 삭제 0줄로 **기존 줄 무변경(추가만)** 이 기계적으로 확증된다.

## 2. 계획 대비 구현 대응표

| 계획 항목 | 구현 결과 |
|---|---|
| §1 record 위치 = `Program.cs` 하단, 전역 네임스페이스 | 그대로 적용 (`HealthResponse`, `WeatherForecast` 뒤) |
| §1 상태 상수 미도입, 핸들러에 리터럴 `"Healthy"` | 그대로 적용 |
| §1 `static` 람다가 record 직접 반환(`TypedResults` 미사용) | 그대로 적용 |
| §1 직렬화는 `JsonSerializerDefaults.Web` 위임, `[JsonPropertyName]` 없음 | 그대로 적용. 실측 결과 `status`/`generatedAt` camelCase 로 직렬화됨(Fact 1 통과가 증거) |
| §2.1 / §2.2 코드 스니펫 | 문자 단위로 반영. `<remarks>` Memory Allocation 항목만 줄바꿈 위치를 조정(내용 동일) |
| §4 주석 규칙(선언부 인라인 근거 + `<remarks>` 3축) | `_factory`·`client`·`response`·`doc` 4개 선언에 내부 동작 근거 주석, 클래스·생성자·Fact 2건에 `<remarks>` 3축 작성. `LinkGenerator` 는 계획 §4대로 선언부 규칙 비대상으로 두고 `<remarks>` 로만 설명 |
| §6 Fact 1 (8단계 단정) | 전부 구현. 상태코드·`application/json`·루트 Object·`status` 키/종류/값·`generatedAt` 키/종류·원시 문자열 오프셋 접미사·`TryGetDateTimeOffset`·`Offset == TimeSpan.Zero`·`InRange(startedAt, finishedAt)` |
| §6 Fact 2 (동기 `public void`, `LinkGenerator`) | 그대로 적용 |
| §7 검증 순서 | 1→8 순서대로 실행. 원문은 `20_test_results.txt` |
| §8 비범위 | 전부 준수(`.Produces<>()`·`TypedResults`·DI 추상화·503·`TimeProvider`·`MapGroup`·`plan/` 문서·패키지·커밋 없음) |

## 3. 최종 재검토(APPROVE) 비차단 지침 4건 반영

1. **CS8602 예방** — `var raw = gen.GetString();` → `Assert.NotNull(raw);` 로 먼저 확정한 뒤 `EndsWith` 를 호출했다. 빌드 결과 **경고 0** 으로 CS8602 미발생이 실측 확인되어 `!` 억제 연산자를 쓰지 않았다.
2. **`<remarks>` 문구 정확성** — Fact 1 Memory Allocation 을 "`using` Dispose + `JsonDocument` 대여 버퍼 풀 반환" 과 "응답 문자열 등 관리 객체는 GC 대상" 으로 분리 기술. Fact 2 Blocking 은 "동기 실행. 최초 `_factory.Services` 접근에는 호스트 초기화 비용·대기가 포함될 수 있다", Memory Allocation 은 "경로 문자열 등 조회 결과. 최초 호출 시 호스트 초기화 할당은 fixture 소관" 으로 기술했다. 계획 §4 표 71행의 Fact 2 "즉시 반환" 문구는 이 지침으로 **대체**했다(자기모순 제거). 클래스 `<remarks>` 의 Blocking 축도 Fact 1(비동기 `await`)·Fact 2(동기)를 명시적으로 나눠 두 멤버 어느 쪽과도 모순되지 않는다.
3. **변경 범위 비교 명령** — `git status --short --untracked-files=all -- . ':(exclude)_workspace'` 로 실행해 미추적 디렉터리가 한 줄로 접히지 않게 했다(§2 표 참조).
4. **커밋 금지** — 커밋·`.git/` 파일 생성 모두 하지 않았다.

## 4. 계획 대비 미세 조정 (설계 변경 아님, 전부 기록)

| 위치 | 계획 문구 | 실제 구현 | 사유 |
|---|---|---|---|
| Fact 1 접미사 검사 | `raw.EndsWith("Z") \|\| raw.EndsWith("+00:00")` | `EndsWith("Z", StringComparison.Ordinal)` / `EndsWith("+00:00", StringComparison.Ordinal)` | 인자 1개 `string.EndsWith` 는 현재 문화권 비교라 문화권 설정에 따라 결과가 흔들릴 수 있다. 판정 의미는 완전히 동일하며 ASCII 리터럴에 대한 Ordinal 비교가 의도의 정확한 표현이다. 설계·범위 변화 없음 |
| Fact 1 `Assert.True` | 메시지 없음 | `TryGetProperty`·접미사·`TryGetDateTimeOffset` 3곳에 실패 메시지 추가 | `Assert.True` 실패 시 기본 메시지가 "Expected: True"뿐이라 회귀 진단이 불가능. 단정 조건 자체는 계획과 동일 |
| §2.2 `<remarks>` | Memory Allocation 1줄 | 동일 문장을 2줄로 줄바꿈 | 가독성. 문자열 내용 동일 |

## 5. 계획의 조건부 분기 — 발동 여부

| 조건부 항목 | 발동 | 근거 |
|---|---|---|
| §5 307 폴백(HTTPS `BaseAddress` 로 재요청) | **미발동** | 기본 `_factory.CreateClient()` 요청이 `HttpStatusCode.OK` 단정을 통과했다(Fact 1 통과). TestServer 에는 `HTTPS_PORT`/서버 주소 피처가 없어 `UseHttpsRedirection()` 이 경고 후 통과하는 계획 §5 예측이 실측으로 확인됨. 기존 `WeatherForecastEndpointTests` 가 동일 파이프라인에서 200 을 받는 것과도 일치 |
| §6 Fact 2 대안(`EndpointDataSource` 열거) | **미발동** | `_factory.Services.GetRequiredService<LinkGenerator>()` → `GetPathByName("GetHealth", values: null)` 이 `/health` 를 정상 반환(Fact 2 통과) |
| §6 CS0121 대비 `(object?)null` 캐스팅 | **불필요** | 빌드 경고·오류 0. 다른 오버로드의 첫 매개변수가 `HttpContext` 라 `string` 과 바인딩되지 않아 오버로드 모호성이 발생하지 않음 |
| §7.4 base_sha 베이스라인 빌드 비교 | **수행** | 코드 변경 전 `dotnet build -c Release` 가 경고 0, 변경 후에도 경고 0 → **신규 경고 0** |

## 6. 검증 결과 요약 (원문: `20_test_results.txt`)

| 게이트 | 명령 | 결과 |
|---|---|---|
| 빌드(베이스라인) | `dotnet build WebProject.sln -c Release` | 오류 0 / 경고 0 |
| 빌드(구현 후) | `dotnet build WebProject.sln -c Release` | 오류 0 / 경고 0 → 신규 경고 0 |
| 필터 테스트 | `dotnet test WebProject.sln -c Release --no-build --filter "FullyQualifiedName~HealthEndpointTests"` | 전체 2 / 통과 2 / 실패 0 / 건너뜀 0, 종료 코드 0 |
| 전체 테스트 | `dotnet test WebProject.sln -c Release --no-build` | 전체 11 / 통과 11 / 실패 0 / 건너뜀 0, 종료 코드 0 (회귀 0) |

**보고 JSON 수치 기준:** 첫 줄 JSON 의 `tests` 는 **전체 테스트 실행(11/0/0)** 수치이며, 필터 실행(2/2)은 위 표와 결과 파일에 별도 보존했다. `changed_files` 는 계획 §2의 **제품·테스트 파일 2개**를 센 값이고, `20_impl_notes.md`·`20_test_results.txt` 는 run 산출물이라 포함하지 않았다.

## 7. [범위 이탈]

**없음.** 계획 §2가 허용한 파일 2개 밖을 수정하지 않았고, 신규 공개 API 는 계획 §3이 예고한 `HealthResponse` record 와 `GET /health` HTTP 계약뿐이며, 설계 변경·파일 추가·패키지 추가는 없었다.

## 8. 후속(승계, 비차단)

- 계획 §9 그대로 승계: (a) Production 에서 HTTPS 포트가 결정되는 환경이면 평문 `/health` 접근이 307 이 된다 — 요구사항 밖이라 파이프라인 유지. (b) 기존 테스트 3파일은 생성자·Fact 에 XML 주석이 없어 신규 파일과 문서화 수준 차이가 있다 — 기존 무변경 원칙 우선, 일괄 정비는 후속 과제.
- `WebProject.Api/Program.cs`·신규 테스트 파일은 LF 개행으로 기록되어 있어 git 이 `core.autocrlf` 에 따라 CRLF 로 정규화한다는 경고를 출력한다(기존 파일과 동일한 조건, 내용 영향 없음).
