# Codex 프롬프트 템플릿 — 통합 계획 재검토 (Phase 1 / 확정 전)

---

두 모델의 계획과 상호 검토를 반영해 통합된 최종 계획이 작성되었다. 구현 착수 전 마지막 검토다.

## 요구사항 및 컨텍스트
# Cross-Verify 컨텍스트 — `/health` 엔드포인트 추가

## 요구사항 원문 (하네스 검증용 소형 기능)
`WebProject.Api`에 `GET /health` 최소 API 엔드포인트를 추가한다.
- 응답: HTTP 200, JSON 객체 `{ "status": "Healthy", "generatedAt": "<UTC 시각, ISO 8601>" }`
- `status`는 상수 문자열 `"Healthy"`(현 단계에서는 외부 의존성 점검 없음).
- `generatedAt`은 서버가 응답을 생성한 UTC 시각(`DateTimeOffset.UtcNow`).
- 엔드포인트 이름(`WithName`)은 `GetHealth`.
- 통합 테스트 1개 이상을 `WebProject.Api.Tests`에 추가: 200 확인, `status == "Healthy"`, `generatedAt`이 UTC(오프셋 0)이며 테스트 시작 시각과 현재 시각 사이에 있음.
- 기존 `/weatherforecast` 동작과 기존 테스트는 변경하지 않는다.

## 프로젝트 규칙 요약 (CLAUDE.md / AGENTS.md)
- 모든 public 타입·멤버에 XML 문서 주석(`///`)을 상세히 작성하고 `<remarks>`에 **Thread Safety / Memory Allocation / Blocking 여부**를 반드시 명시한다.
- 네트워크·메모리 관련 타입(`HttpClient`, `WebApplicationFactory`, `Channel<T>`, `ArrayPool<T>` 등)을 선언할 때는 "왜 이 타입인가"를 **내부 동작 메커니즘** 근거로 인라인 `//` 주석으로 단다.
- 절대 경로 하드코딩 금지. 커밋은 하지 않는다(Stop 훅/commitandpush가 담당).
- 테스트 프로젝트 스타일: xUnit 2.9.3, `IClassFixture<WebApplicationFactory<Program>>`, `WebApplicationFactory<Program>`·`HttpClient` 선언부에 근거 주석, 클래스 `<remarks>` 작성. 예시: `WebProject.Api.Tests/WeatherForecastEndpointTests.cs`.
- 응답 모델은 `record`를 선호(예: `WeatherForecast` record, `Program.cs` 하단).

## 관련 코드 경로·현재 구조
- `WebProject.Api/Program.cs` — 최소 API 진입점. `AddOpenApi`, `UseHttpsRedirection`, `/weatherforecast` `MapGet`, `WeatherForecast` record, `public partial class Program { }`.
- `WebProject.Api/WebProject.Api.csproj` — `Microsoft.NET.Sdk.Web`, net10.0.
- `WebProject.Api.Tests/` — `WeatherForecastEndpointTests.cs`(통합), `WeatherForecastTests.cs`, `WeatherForecastRangeTests.cs`(단위). `Microsoft.AspNetCore.Mvc.Testing 10.0.12`.
- 솔루션: `WebProject.sln` (Api, Api.Tests, Sample). CI 게이트: `dotnet test WebProject.sln`.

## 제약
- net10.0, 새 NuGet 패키지 추가 금지.
- `Program.cs`의 기존 코드는 최소 변경(엔드포인트 추가와 응답 record 추가만).
- 응답 record는 `Program.cs`에 두거나 별도 파일 `WebProject.Api/HealthResponse.cs`로 분리 가능(계획에서 선택·근거 제시).

## 기준 커밋
- base_sha: 524f5a730867ce59e226bd4f9332961b8b8df6f8 (master, 작업 트리 깨끗함 확인)

## 비범위 (non-goals)
- `Microsoft.Extensions.Diagnostics.HealthChecks` 도입, DB/외부 의존성 점검, 인증, 레이트 리밋, OpenAPI 스키마 커스터마이징, `/weatherforecast` 변경.

## 의견 조정 결과 (채택/기각/미해결 기록)
# 12. Plan 조정 기록 (라운드 1) — `GET /health`

- run: `20260913_070000_health-endpoint`, base_sha `524f5a73…`
- 입력: `10_claude_plan.md`, `10_codex_plan.md`, `11_codex_check_of_claude_plan.md`(Codex→Claude 계획), `11_claude_check_of_codex_plan.md`(Claude→Codex 계획)
- 조정 주체: 오케스트레이터(메인 세션). 판정 근거는 코드·규칙 원문. 양측 High 0건.

## A. Codex 가 Claude 계획에 제기한 지적 (P-X1~X10 + 취향 2)

| ID | 심각도 | 판정 | 근거 / 통합 계획 반영 |
|---|---|---|---|
| P-X1 재빌드 없는 `--no-build` 테스트 | Med | **채택** | Claude §7 순서(3 빌드 → 4 테스트 작성 → 5 `--no-build`)는 신규 테스트가 DLL 에 없어 증빙이 아니다. 통합 계획은 **코드·테스트 전부 작성 후** `dotnet build` → `dotnet test --no-build`로 재배열하고, 필터 실행 시 "실행된 테스트 ≥ 2" 를 확인한다. |
| P-X2 DTO 역직렬화만으로 키 계약 미검증 | Med | **채택** | 웹 기본 역직렬화는 대소문자 무시(`PropertyNameCaseInsensitive=true`)라 `"Status"` 응답도 통과한다. `JsonDocument` 로 정확한 키 `status`/`generatedAt` 와 값 종류(String) 를 검사한다(Codex 계획 §5 절차 채택). |
| P-X3 테스트 생성자·메서드 XML 주석 누락 | Med | **채택** | CLAUDE.md 규칙은 "public 클래스의 메서드" 전부를 대상으로 한다. 신규 테스트 클래스의 public 생성자·`[Fact]` 메서드에 `<summary>` 작성, 클래스 `<remarks>` 에 Blocking 축 추가("요청 완료를 비동기로 대기"). 기존 테스트는 손대지 않는다. |
| P-X4 소형 기능 사유로 `plan/` 문서 면제 | Med | **부분 채택** | "규모 예외" 논리는 철회한다(규칙에 규모 예외 없음). 다만 **오케스트레이터 정책**으로 이번 run 은 `plan/` 문서를 만들지 않는다: 이 run 디렉터리(`00_context`·`13_final_plan`·`90_final_report`)가 git 추적되는 설계 기록이고, 컨텍스트가 변경 범위를 3파일로 고정했다. Claude 검토 미해결 질문 1 도 같은 판정으로 종결. |
| P-X5 HTTPS 기준 주소·자동 리디렉션 비활성화 요구 | Med | **기각** | `HttpsRedirectionMiddleware` 는 https 포트를 `HTTPS_PORT`/`ASPNETCORE_HTTPS_PORT` 설정 또는 `IServerAddressesFeature` 에서 찾는데 `WebApplicationFactory`+`TestServer` 에는 둘 다 없어 경고 로그 후 통과(pass-through)한다. 설령 리디렉션이 발생해도 자동 추적 후 도달하는 곳 역시 같은 TestServer 이므로 요구사항(200·본문) 판정이 달라지지 않는다. 기존 테스트와 다른 클라이언트 설정은 컨텍스트가 정본으로 지정한 스타일과 갈린다(Claude P-C1 과 동일 결론). 구현 시 실제 307 이 관측되면 테스트 측 `AllowAutoRedirect` 로 대응(Claude §5 표 유지). |
| P-X6 `GetHealth` 이름 회귀 테스트 없음 | Low | **채택** | Fact 2 로 승격(필수). 메커니즘은 Claude P-C2 판정 참조. |
| P-X7 "플래키하지 않다" 단정 과함 | Low | **채택** | 통합 계획 문구를 "동일 프로세스·동일 시계 조건에서 안정적이며 시스템 시계 보정·역행에는 영향받을 수 있다" 로 한정. 재시도·`TimeProvider` 도입 없음. |
| P-X8 불필요한 공개 상수 `HealthyStatus` | Low | **채택** | 핸들러 1곳만 쓰고 테스트는 리터럴 단정이라 중복 제거 효과가 없다. 공개 표면을 늘리지 않기 위해 상수를 두지 않는다(Claude 취향 3 기각과 짝). |
| P-X9 "런타임 예외 경로 없음" 과일반화 | Low | **채택** | "도메인 실패 경로 없음, 직렬화·전송 오류는 ASP.NET Core 기본 처리에 위임" 으로 문구 교정. |
| P-X10 `git diff --stat` 만으로 신규 파일 범위 증명 | Low | **채택** | 시작·종료 시점 `git status --short` 비교 + 추적 파일 diff + 신규 파일 본문 확인으로 교체. 기존 미추적(`_workspace/`)은 제외. |
| [취향] record 파일 배치 | — | **Program.cs 하단 채택** | 컨텍스트 제약 문구("엔드포인트 추가와 응답 record 추가만") 가 `Program.cs` 내 record 추가를 이미 예정하고, 기존 `WeatherForecast` 와 동일 배치로 파일 수 불변. Claude §1.1 의 "최소 변경 상충" 근거는 Codex 검토대로 철회(허용된 대안). Claude 미해결 질문 3 종결. |
| [취향] `static` 람다 | — | **채택** | 캡처 방지 표기로 비용 0. `DateTimeOffset.UtcNow` 선택 근거 인라인 주석(Claude §2.1)은 유지. |

## B. Claude 가 Codex 계획에 제기한 지적 (P-C1~C5 + 취향 4)

| ID | 심각도 | 판정 | 근거 / 통합 계획 반영 |
|---|---|---|---|
| P-C1 HTTPS 회피 전략의 잘못된 전제 | Med | **채택** | A 표 P-X5 판정과 동일. 기본 `CreateClient()` 사용. |
| P-C2 이름 검증 테스트의 파일 목록 누락·메커니즘 미정 | Med | **채택** | 같은 파일 `HealthEndpointTests.cs` 안의 Fact 2 로 확정. 메커니즘: `_factory.Services.GetRequiredService<LinkGenerator>().GetPathByName("GetHealth", values: null)` 이 `"/health"` 를 반환하는지 단정(`WithName` 은 `IEndpointNameMetadata` 를 등록하고 `LinkGenerator` 는 그 이름으로 엔드포인트를 조회하므로 이름·경로를 동시에 검증). 대안(`EndpointDataSource` 열거)은 구현자가 `LinkGenerator` 경로가 실패할 때만 사용. Claude 미해결 질문 2(필수 여부) → **필수** 로 종결. |
| P-C3 camelCase 근거 미기재 | Low | **채택** | 최소 API 는 `JsonSerializerDefaults.Web`(camelCase) 로 직렬화하므로 `[JsonPropertyName]` 불필요. 통합 계획에 명기하고 Fact 1 의 원시 키 검사가 안전망. |
| P-C4 `JsonDocument` 선언 인라인 근거 주석 누락 | Low | **채택** | `JsonDocument` 는 `ArrayPool<byte>` 에서 빌린 버퍼에 UTF-8 원문을 보관하므로 `using` 으로 반환해야 한다는 근거 주석을 선언부에 단다(메모리 관련 타입 규칙 대상). |
| P-C5 콘텐츠 타입 단언 방식 미정 | Low | **채택** | `response.Content.Headers.ContentType?.MediaType == "application/json"` 으로 비교(charset 파라미터 무시). |
| [취향] CI Release 구성 정렬 | — | **채택** | 실행 명령을 `-c Release` 로 통일(Codex 검토도 통합 가치 인정). |
| [취향] `+00:00` 단독 단정으로 좁히기 | — | **기각(반대 방향 채택)** | 문자열 접미사 단정은 표기 방식 변화에 취약하다. Codex 계획의 "`Z` 또는 `+00:00` 존재" 검사도 제거하고 `JsonElement.TryGetDateTimeOffset` 성공 + `Offset == TimeSpan.Zero` 로만 UTC 를 검증한다(요구사항이 요구하는 것은 오프셋 0). Claude 미해결 질문 1(`+00:00` 허용) 은 요구사항 `DateTimeOffset.UtcNow` 명시로 종결. |
| [취향] 상태 값 상수화 | — | **기각** | A 표 P-X8 채택과 상충. 테스트가 리터럴을 단정해야 계약 회귀를 잡는다는 Claude 계획 §6.1 논지를 유지하면 상수의 존재 이유가 없다. |
| [취향] 시계 역행 한계 기술 유지 | — | **채택** | P-X7 과 동일 문구. |

## C. 양측 계획의 미해결 질문 처리

| 출처 | 질문 | 처리 |
|---|---|---|
| Claude 계획 Q1 | `+00:00` 표기 허용 여부 | 종결 — 요구사항이 `DateTimeOffset.UtcNow` 를 명시. 검증은 오프셋 0. |
| Claude 계획 Q2 | Production 에서 `/health` 가 HTTPS 리디렉션 뒤에 놓여도 되는가 | **승계(비차단)** — 요구사항·비범위에 언급 없음. 다른 엔드포인트와 동일 파이프라인 유지. 프로브가 평문 접근할 계획이면 별도 요구사항으로 다룬다. `90_final_report` 에 승계. |
| Claude 계획 Q3 | record 배치 통일 | 종결 — `Program.cs` 하단. |
| Claude 검토 Q1 | `plan/` 문서 | 종결 — 미작성(P-X4 판정). |
| Claude 검토 Q2 | 이름 검증 필수 여부 | 종결 — 필수(Fact 2). |

## D. 7축 확인

| 축 | 결과 |
|---|---|
| 요구사항 누락 | 없음. 200·`status`·`generatedAt` UTC·시각 창·`WithName`·기존 테스트 무변경 전부 테스트로 고정. |
| 잘못된 가정 | Codex P-X5 전제(리디렉션 발생 가능) 기각, Claude §1.1 "최소 변경 상충" 철회. |
| 과도한 설계 | 공개 상수 제거, HealthChecks·TimeProvider·Produces 미도입 유지. |
| 기존 구조 충돌 | 없음. `WeatherForecast` 와 동일 배치·동일 테스트 구조. |
| 예외 처리 | 도메인 실패 경로 없음(문구 교정). |
| 테스트 전략 | 원시 JSON 키 검사 + 오프셋 0 + 시각 창 + `LinkGenerator` 이름 검증, 작성 후 빌드 순서. |
| 프로젝트 규칙 | record·테스트 클래스/생성자/메서드 XML 주석, `<remarks>` 3축, `WebApplicationFactory`/`HttpClient`/`JsonDocument` 선언부 근거 주석. |

## E. 통계

| 구분 | 채택 | 부분 채택 | 기각 | 승계 |
|---|---|---|---|---|
| Codex→Claude (10 + 취향 2) | 10 | 1 | 1 | 0 |
| Claude→Codex (5 + 취향 4) | 7 | 0 | 2 | 0 |
| 미해결 질문 5 | 4 종결 | — | — | 1 |

## 검토 대상: 통합 최종 계획
# 13. 통합 최종 계획 (라운드 1) — `GET /health` 엔드포인트

- run: `20260913_070000_health-endpoint`, base_sha `524f5a730867ce59e226bd4f9332961b8b8df6f8`
- 근거: `10_claude_plan.md` + `10_codex_plan.md` + 조정 `12_plan_adjudication.md`
- 반영 내역: Codex→Claude 지적 P-X1·X2·X3·X6·X7·X8·X9·X10 채택, P-X4 부분 채택, P-X5 기각. Claude→Codex 지적 P-C1~C5 채택. 취향은 record `Program.cs` 배치·`static` 람다·Release 구성·접미사 단정 제거·상수 미도입으로 확정.

## 1. 설계 결정 (확정)

| 항목 | 결정 | 근거 |
|---|---|---|
| 응답 record 위치 | `WebProject.Api/Program.cs` 하단, `WeatherForecast` 뒤·`public partial class Program` 앞 | 기존 배치 일관, 파일 수 불변, 컨텍스트 제약 문구가 record 추가를 예정 (조정 A 취향 1) |
| 네임스페이스 | 전역(기존 `WeatherForecast` 와 동일) | 어셈블리 내 배치 규칙 통일 |
| 상태 상수 | **두지 않는다.** 핸들러에 리터럴 `"Healthy"` | P-X8 채택 |
| 핸들러 | `static` 람다가 record 를 직접 반환(`TypedResults` 미사용) | 기존 스타일, 캡처 방지 |
| 직렬화 | 기본 `JsonSerializerDefaults.Web`(camelCase) 에 위임 → `status`/`generatedAt`. `[JsonPropertyName]` 없음 | P-C3 |
| UTC 표기 | `DateTimeOffset.UtcNow` → `…+00:00`. 테스트는 접미사 문자열을 단정하지 않고 파싱 후 `Offset == TimeSpan.Zero` | 조정 B 취향 2 |
| `plan/` 문서 | 작성하지 않음(이 run 디렉터리가 추적되는 설계 기록) | P-X4 부분 채택 |

## 2. 변경 파일 (정확히 3개, 그 외 무변경)

| # | 파일 | 종류 | 요지 |
|---|---|---|---|
| 1 | `WebProject.Api/Program.cs` | 수정 | (a) `.WithName("GetWeatherForecast");` 뒤·`app.Run();` 앞에 `/health` `MapGet` 블록 삽입. (b) 파일 하단 `WeatherForecast` record 뒤에 `HealthResponse` record 추가(XML 주석 + `<remarks>`). 그 외 기존 줄 무변경. |
| 2 | `WebProject.Api.Tests/HealthEndpointTests.cs` | 신규 | `IClassFixture<WebApplicationFactory<Program>>` 통합 테스트. Fact 1(HTTP·JSON 계약·UTC·시각 창), Fact 2(`LinkGenerator` 로 `GetHealth` 이름 검증). |
| 3 | — | — | (변경 파일은 위 2개. csproj·sln·appsettings·기존 테스트·`WebProject.Sample`·CI 무변경. 패키지 추가 없음.) |

### 2.1 `Program.cs` 삽입 코드

```csharp
app.MapGet("/health", static () =>
    // DateTimeOffset.UtcNow: 로컬 타임존 변환(tzdata/레지스트리 조회)을 거치지 않고 시스템 UTC 틱을
    // 그대로 읽으므로 오프셋 0 이 보장되고 Now 보다 호출 비용이 낮다.
    new HealthResponse("Healthy", DateTimeOffset.UtcNow))
    .WithName("GetHealth");
```

### 2.2 `HealthResponse` record (Program.cs 하단)

```csharp
/// <summary>서비스 생존 여부를 알리는 <c>/health</c> 응답 모델.</summary>
/// <param name="Status">서비스 상태. 현 단계에서는 외부 의존성 점검 없이 상수 <c>"Healthy"</c> 를 반환한다.</param>
/// <param name="GeneratedAt">서버가 응답을 생성한 UTC 시각(오프셋 0).</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 record 이며 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 요청당 record 인스턴스 1개 힙 할당. <paramref name="Status"/> 는 상수 문자열 인터닝으로 추가 할당 없음. JSON 직렬화·응답 전송 버퍼는 프레임워크 소관.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O·대기 없음.</description></item>
/// </list>
/// </remarks>
public record HealthResponse(string Status, DateTimeOffset GeneratedAt);
```

## 3. 공개 API 영향

- 신규 HTTP 계약: `GET /health` → 200, `application/json`, `{ "status": "Healthy", "generatedAt": "<ISO 8601, +00:00>" }`, 엔드포인트 이름 `GetHealth`(기존 이름 `GetWeatherForecast` 와 충돌 없음).
- 신규 공개 타입: `HealthResponse(string Status, DateTimeOffset GeneratedAt)` (record 자동 멤버 포함). 공개 상수 없음.
- OpenAPI(Development 한정)에 `/health` 자동 포함. 커스터마이징 없음.
- 기존 계약 변경 없음.

## 4. 동시성·메모리 정책과 주석 규칙 적용

| 대상 | 규칙 | 내용 |
|---|---|---|
| `HealthResponse` | `<remarks>` 3축 필수 | §2.2 |
| `HealthEndpointTests` 클래스 | `<remarks>` 필수 | Thread Context(xUnit 테스트 스레드, 인메모리 TestServer) / Memory Policy(`IClassFixture` 1회 생성, `JsonDocument` 풀 버퍼 `using` 반환) / Concurrency(팩토리·HttpClient thread-safe) / **Blocking: 각 테스트는 요청 완료를 `await` 로 비동기 대기하며 동기 블로킹 없음** |
| 테스트 public 생성자·`[Fact]` 메서드 | `<summary>` 필수 (P-X3) | 무엇을 검증하는지 1~2줄 |
| `WebApplicationFactory<Program> _factory` 선언 | 인라인 근거 주석 | 인메모리 TestServer 호스팅으로 커널 소켓·TCP 핸드셰이크 없이 라우팅·미들웨어·직렬화 전 경로 검증 |
| `HttpClient client` 선언 | 인라인 근거 주석 | `HttpMessageHandler` 가 TestServer 파이프라인에 직결되어 네트워크 스택·포트 점유 없음 |
| `JsonDocument` 선언 | 인라인 근거 주석 (P-C4) | UTF-8 원문을 `ArrayPool<byte>` 대여 버퍼에 보관하므로 `using` 으로 반환해야 풀 누수·GC 압력이 없음 |
| `/health` 핸들러 | 선언부 규칙 비대상(네트워크·메모리 타입 선언 없음) | `DateTimeOffset.UtcNow` 근거 1줄만 |

런타임: 공유 상태 없음, 요청마다 새 record, 락 없음.

## 5. 예외·실패 경로

- 입력·외부 I/O 가 없어 **도메인 실패 경로는 없다.** 직렬화·전송 오류는 ASP.NET Core 기본 처리에 위임하며 `try/catch`·`ProblemDetails`·503 분기를 만들지 않는다 (P-X9).
- `UseHttpsRedirection()`: TestServer 에는 https 포트 정보가 없어 통과. 기본 `CreateClient()` 사용 (P-X5 기각·P-C1). 구현 중 307 이 실제 관측되면 미들웨어를 옮기지 않고 테스트 클라이언트 `AllowAutoRedirect=false` + 사유를 `20_impl_notes.md` 에 기록.
- `WithName` 중복 시 시작 예외 → 통합 테스트가 즉시 실패해 포착.
- 시각 창 단정은 동일 프로세스·동일 시계 조건에서 안정적이며, 시스템 시계 보정·역행에는 영향받을 수 있다. 재시도·`TimeProvider` 도입 없음 (P-X7).

## 6. 테스트 전략 — `WebProject.Api.Tests/HealthEndpointTests.cs`

공통: `namespace WebProject.Api.Tests;`, `public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>`, 생성자 주입 `_factory`. 프로덕션 타입 `HealthResponse` 를 참조하지 않는다.

**Fact 1 — `GetHealth_Returns200WithHealthyStatusAndUtcTimestamp`**
1. `var startedAt = DateTimeOffset.UtcNow;`
2. `using var client = _factory.CreateClient();` → `using var response = await client.GetAsync("/health");`
3. `Assert.Equal(HttpStatusCode.OK, response.StatusCode)`; `Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType)` (P-C5).
4. `using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());` 루트가 `JsonValueKind.Object`.
5. `doc.RootElement.TryGetProperty("status", out var status)` 참, `status.ValueKind == String`, `status.GetString() == "Healthy"` (정확한 키·리터럴 값, P-X2).
6. `TryGetProperty("generatedAt", out var gen)` 참, `gen.ValueKind == String`, `gen.TryGetDateTimeOffset(out var generatedAt)` 참(ISO 8601 파싱 가능). 접미사 문자열 단정 없음.
7. `Assert.Equal(TimeSpan.Zero, generatedAt.Offset)`.
8. `var finishedAt = DateTimeOffset.UtcNow; Assert.InRange(generatedAt, startedAt, finishedAt);` (양끝 포함).

**Fact 2 — `GetHealth_IsNamedGetHealth`** (필수, P-X6·P-C2)
- `var linkGenerator = _factory.Services.GetRequiredService<LinkGenerator>();`
- `Assert.Equal("/health", linkGenerator.GetPathByName("GetHealth", values: null));`
- `LinkGenerator` 가 `IEndpointNameMetadata` 로 엔드포인트를 찾으므로 이름 삭제·오타 시 `null` 이 반환돼 실패한다. 이 경로가 이 SDK 에서 실패하면(예: 호스트 미기동) 대안으로 `EndpointDataSource.Endpoints` 를 열거해 `RouteEndpoint.RoutePattern.RawText == "/health"` 인 엔드포인트의 `Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetHealth"` 를 단정하고 사유를 `20_impl_notes.md` 에 적는다.

기존 테스트 3파일은 한 글자도 수정하지 않는다.

## 7. 구현·검증 순서 (P-X1 반영)

1. 시작 시점 `git status --short` 기록(기존 미추적 `_workspace/` 는 제외 대상).
2. `WebProject.Api/Program.cs` — `HealthResponse` record 추가 + `/health` `MapGet` 삽입.
3. `WebProject.Api.Tests/HealthEndpointTests.cs` 작성(주석 규칙 §4 포함).
4. `dotnet build WebProject.sln -c Release` — 오류 0, **기준 커밋 대비 신규 경고 0**(필요 시 base_sha 빌드 경고를 베이스라인으로 비교. 두 csproj 모두 `GenerateDocumentationFile` 미설정이라 CS1591 없음).
5. `dotnet test WebProject.sln -c Release --no-build --filter "FullyQualifiedName~HealthEndpointTests"` — **실행된 테스트 2개·통과 2개** 확인.
6. `dotnet test WebProject.sln -c Release --no-build` — 전체 통과(회귀 0). 결과 원문을 `20_test_results.txt` 에 보존.
7. 종료 시점 `git status --short` 를 1 과 비교 — 변경은 `M WebProject.Api/Program.cs`, `?? WebProject.Api.Tests/HealthEndpointTests.cs` 만(P-X10). `git diff` 로 기존 줄 무변경 확인.
8. 커밋하지 않는다(Stop 훅 담당). `20_impl_notes.md` 에 결정·이탈·관측 사항 기록.

## 8. 비범위 (non-goals)

컨텍스트 비범위(HealthChecks 패키지·DB/외부 점검·인증·레이트 리밋·OpenAPI 커스터마이징·`/weatherforecast` 변경) + `.Produces<>()`, `TypedResults`, `IHealthProvider`/DI 추상화, 캐싱·압축·로깅·503, `TimeProvider` 주입, `MapGroup`, 기존 코드 리팩토링, 공개 상수, `plan/` 문서, 패키지 추가, 커밋.

## 9. 승계 미해결(비차단)

- Production 에서 `/health` 가 `UseHttpsRedirection()` 뒤에 있어 평문 접근 시 307. 요구사항 밖이므로 동일 파이프라인 유지. `90_final_report.md` 에 승계.

## 지시
1. 조정 결과에서 "채택"된 의견이 통합 계획에 실제로 반영되었는지 항목별로 대조하라.
2. 통합 과정에서 새로 생긴 모순·누락이 있는지 확인하라.
3. 남은 미해결 항목 중 구현을 막을 만큼 중대한 것이 있는지 판정하라.

마지막 줄에 반드시 `VERDICT: APPROVE` 또는 `VERDICT: REQUEST-CHANGES`를 출력하고, REQUEST-CHANGES면 바로 위에 사유를 번호 목록으로 기술하라. 한국어로 작성하고 파일을 수정하지 마라.
