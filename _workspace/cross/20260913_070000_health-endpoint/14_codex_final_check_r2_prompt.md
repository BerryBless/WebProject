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
라운드 1 재검토에서 Codex 가 REQUEST-CHANGES 로 제기한 4건과 Claude 의 Low 3건을 처리한 라운드 2 조정 기록이다.

# 12. Plan 조정 기록 (라운드 2) — 14단계 재검토 지적 처리

- 입력: `14_claude_final_check.md`(VERDICT: APPROVE, Low 3), `14_codex_final_check.md`(VERDICT: REQUEST-CHANGES, Med 3·Low 1), `13_final_plan.md`(r1)
- 라운드 1 조정(`12_plan_adjudication.md`)의 판정은 아래 재판정 항목 외 전부 유지한다.

## A. Codex 재검토 지적 (REQUEST-CHANGES 사유 4 + 대조표 비고)

| ID | 심각도 | 판정 | 근거 / r2 반영 |
|---|---|---|---|
| X-F1 오프셋 없는 문자열이 UTC 환경에서 `Offset==0` 검사를 통과 | Med | **채택 (r1 취향 판정 번복)** | STJ 의 `JsonHelpers.TryParseAsISO` 는 오프셋 부재 시 `DateTimeKind.Unspecified` 로 읽고 `DateTimeOffset` 변환 시 **로컬 오프셋**을 붙인다. CI(`windows-latest`)와 UTC 서버에서는 로컬 오프셋이 0 이라 `"…T00:00:00"` 같은 결함 응답도 통과한다. 따라서 Fact 1 에 **원시 문자열이 `Z` 또는 `+00:00` 으로 끝나는지** 검사를 추가한다(둘 다 허용해 특정 표기에 고정하지 않음). r1 조정 B 취향 2("접미사 단정 제거")는 "단독 접미사 고정 금지" 로 축소 재판정. `TryGetDateTimeOffset` + `Offset==0` + 시각 창 검사는 유지. |
| X-F2 307 관측 시 `AllowAutoRedirect=false` 대응은 307 을 그대로 돌려 200 검증을 못 함 | Med | **채택** | 사실. §5 폴백을 "`_factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") })` 로 요청해 200·본문을 검증하고 사유를 `20_impl_notes.md` 에 기록" 으로 교체. 기본 경로는 여전히 `CreateClient()`(P-C1 유지). `AllowAutoRedirect=false` 는 진단용으로만 언급. |
| X-F3 테스트 생성자·Fact 메서드에 `<remarks>` 3축 필요, "각 테스트는 await" 문구가 동기 Fact 2 와 불일치 | Med | **채택** | CLAUDE.md 는 "public 클래스의 메서드" 에 `<remarks>` 3축(Thread Safety / Memory Allocation / Blocking) 을 요구한다. 신규 생성자·Fact 1·Fact 2 각각에 멤버별 `<remarks>` 를 작성한다. Fact 2 는 `LinkGenerator` 조회만 하므로 **동기 `void` 메서드**로 확정하고 Blocking 을 "즉시 반환" 으로, Fact 1 은 "요청 완료를 `await` 로 비동기 대기" 로 기술한다. 클래스 `<remarks>` 의 Blocking 문구도 메서드별 차이를 반영. (Claude 취향 "기존 테스트와 스타일 갈림" 은 기존 무변경 원칙과 충돌하지 않으므로 신규 파일에만 적용.) |
| X-F4 "정확히 3개" ↔ 실제 2개, 빈 3행, run 디렉터리 산출물 허용 범위, 신규 파일 본문 확인 | Low | **채택** | Claude P-C6 과 동일. §2 를 "제품·테스트 코드 2개" 로 교정, 빈 행 삭제, 종료 검사에서 `_workspace/cross/<run>/**` 를 허용 범위로 명시, `git diff --no-index /dev/null <신규 파일>` 로 본문 확인 추가(P-X10 완전 반영). |
| 대조표 비고: P-X3·P-X10·P-C1 "부분 반영" | — | **채택** | 위 X-F2·X-F3·X-F4 로 각각 해소. |
| 대조표 비고: §9 "평문 접근 시 307" 에 "HTTPS 포트가 결정되는 경우" 조건 | — | **채택** | 문구 교정. |

## B. Claude 재검토 지적 (APPROVE, Low 3 + 취향 1)

| ID | 심각도 | 판정 | 근거 / r2 반영 |
|---|---|---|---|
| P-C6 파일 수 표기 모순 | Low | **채택** | X-F4 와 통합. |
| P-C7 `using var response` 근거 미기록, `LinkGenerator` 선언 규칙 대상 여부 미기재 | Low | **채택** | §4 표에 2행 추가: `HttpResponseMessage` 는 응답 콘텐츠 스트림·버퍼 소유권을 갖는 타입이므로 `using` 으로 테스트 스코프 종료 시 즉시 반환(인라인 근거 주석). `LinkGenerator` 는 라우팅 조회 서비스로 네트워크·메모리 프리미티브가 아니라 선언부 규칙 **비대상** 명기. |
| P-C8 using 지시문 미명시, `GetPathByName` 오버로드 해석 노트 | Low | **채택** | §6 공통에 5개 using(`System.Net`, `System.Text.Json`, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.AspNetCore.Routing`, `Microsoft.Extensions.DependencyInjection`) 명시. CS0121 시 `(object?)null` 캐스팅 노트. |
| [취향] 생성자·Fact `<summary>` 로 기존 파일과 스타일 갈림 | — | **기각(방향 반대)** | X-F3 채택으로 신규 파일은 오히려 더 상세히 문서화한다. 기존 테스트는 무변경 원칙대로 두고 일괄 정비는 후속 과제로 `90_final_report` 에 기록. |

## C. 양측 검증 결과 중 채택한 사실 확인
- Claude (a): `_factory.Services` 접근이 호스트를 기동하고 `EndpointNameAddressScheme` 이 `IEndpointNameMetadata` 로 조회하므로 `GetPathByName("GetHealth", values: null)` → `/health`. Fact 2 메커니즘 확정.
- Claude (b) 와 Codex X-F1 은 양립한다: `+00:00`/`Z` 는 오프셋 0 으로 파싱되지만(Claude), 오프셋 부재는 로컬로 해석된다(Codex). 결론은 X-F1 채택.

## D. 통계 (r2)
| 구분 | 채택 | 기각 |
|---|---|---|
| Codex 4 + 비고 2 | 6 | 0 |
| Claude 3 + 취향 1 | 3 | 1 |

## 검토 대상: 통합 최종 계획
# 13. 통합 최종 계획 (라운드 2) — `GET /health` 엔드포인트

- run: `20260913_070000_health-endpoint`, base_sha `524f5a730867ce59e226bd4f9332961b8b8df6f8`
- 근거: `10_claude_plan.md` + `10_codex_plan.md` + 조정 `12_plan_adjudication.md`(r1) + `12_plan_adjudication_r2.md`(r2: 14단계 재검토 반영)
- 반영 내역: Codex→Claude 지적 P-X1·X2·X3·X6·X7·X8·X9·X10 채택, P-X4 부분 채택, P-X5 기각. Claude→Codex 지적 P-C1~C5 채택. 취향은 record `Program.cs` 배치·`static` 람다·Release 구성·상수 미도입으로 확정.
- **r2 변경:** X-F1(접미사 `Z`/`+00:00` 존재 검사 복원), X-F2(307 폴백을 HTTPS 기준 주소로), X-F3(테스트 생성자·Fact 멤버별 `<remarks>` 3축, Fact 2 동기), X-F4/P-C6(파일 수 2개·허용 범위·신규 파일 본문 확인), P-C7(`using var response`·`LinkGenerator` 규칙 대상 명기), P-C8(using 5종), §9 조건 문구.

## 1. 설계 결정 (확정)

| 항목 | 결정 | 근거 |
|---|---|---|
| 응답 record 위치 | `WebProject.Api/Program.cs` 하단, `WeatherForecast` 뒤·`public partial class Program` 앞 | 기존 배치 일관, 파일 수 불변, 컨텍스트 제약 문구가 record 추가를 예정 (조정 A 취향 1) |
| 네임스페이스 | 전역(기존 `WeatherForecast` 와 동일) | 어셈블리 내 배치 규칙 통일 |
| 상태 상수 | **두지 않는다.** 핸들러에 리터럴 `"Healthy"` | P-X8 채택 |
| 핸들러 | `static` 람다가 record 를 직접 반환(`TypedResults` 미사용) | 기존 스타일, 캡처 방지 |
| 직렬화 | 기본 `JsonSerializerDefaults.Web`(camelCase) 에 위임 → `status`/`generatedAt`. `[JsonPropertyName]` 없음 | P-C3 |
| UTC 표기 | `DateTimeOffset.UtcNow` → `…+00:00`. 테스트는 (1) 원시 문자열이 `Z` 또는 `+00:00` 으로 끝나는지(오프셋 부재 시 STJ 가 로컬로 해석해 UTC 환경에서 결함이 숨는 것을 방지), (2) 파싱 후 `Offset == TimeSpan.Zero` 를 함께 검사. 특정 표기 하나에 고정하지 않음 | r2 X-F1 |
| `plan/` 문서 | 작성하지 않음(이 run 디렉터리가 추적되는 설계 기록) | P-X4 부분 채택 |

## 2. 변경 파일 (제품·테스트 코드 정확히 2개, 그 외 무변경)

| # | 파일 | 종류 | 요지 |
|---|---|---|---|
| 1 | `WebProject.Api/Program.cs` | 수정 | (a) `.WithName("GetWeatherForecast");` 뒤·`app.Run();` 앞에 `/health` `MapGet` 블록 삽입. (b) 파일 하단 `WeatherForecast` record 뒤에 `HealthResponse` record 추가(XML 주석 + `<remarks>`). 그 외 기존 줄 무변경. |
| 2 | `WebProject.Api.Tests/HealthEndpointTests.cs` | 신규 | `IClassFixture<WebApplicationFactory<Program>>` 통합 테스트. Fact 1(HTTP·JSON 계약·UTC·시각 창), Fact 2(`LinkGenerator` 로 `GetHealth` 이름 검증). |

허용되는 그 외 산출물: `_workspace/cross/20260913_070000_health-endpoint/**`(구현 노트·테스트 결과·리뷰 기록, git 추적). csproj·sln·appsettings·기존 테스트·`WebProject.Sample`·CI 무변경. 패키지 추가 없음.

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
| `HealthEndpointTests` 클래스 | `<remarks>` 필수 | Thread Context(xUnit 테스트 스레드, 인메모리 TestServer) / Memory Policy(`IClassFixture` 1회 생성, `JsonDocument` 풀 버퍼 `using` 반환) / Concurrency(팩토리·HttpClient thread-safe) / **Blocking: Fact 1 은 요청 완료를 `await` 로 비동기 대기(동기 블로킹 없음), Fact 2 는 동기 즉시 반환** |
| 테스트 public 생성자 | `<summary>` + `<remarks>` 3축 (r2 X-F3) | Thread Safety: xUnit 이 클래스 인스턴스를 테스트마다 생성, 공유 상태는 fixture 참조뿐 / Memory Allocation: 필드 대입만, 추가 할당 없음 / Blocking: 즉시 반환 |
| `[Fact]` Fact 1 (async Task) | `<summary>` + `<remarks>` 3축 | Thread Safety: 테스트 전용 클라이언트·응답 객체만 사용 / Memory Allocation: `HttpClient`·`HttpResponseMessage`·응답 문자열·`JsonDocument`(풀 버퍼) 할당, 전부 `using` 으로 스코프 종료 시 반환 / Blocking: 요청 완료를 `await` 로 비동기 대기, 동기 블로킹 없음 |
| `[Fact]` Fact 2 (동기 void) | `<summary>` + `<remarks>` 3축 | Thread Safety: DI 에서 싱글턴 `LinkGenerator` 조회만 / Memory Allocation: 경로 문자열 1개 / Blocking: 즉시 반환(첫 호출 시 fixture 호스트 기동은 `_factory.Services` 가 동기 수행) |
| `using var response` (`HttpResponseMessage`) | 인라인 근거 주석 (r2 P-C7) | 응답 콘텐츠 스트림·버퍼 소유권을 갖는 타입이므로 테스트 스코프 종료 시 즉시 반환. 정본 파일(`WeatherForecastEndpointTests.cs:37`)은 `using` 없이 두었으나 신규 파일은 소유권 명시를 택한다(기존 파일 무변경). |
| `LinkGenerator` 선언 | 선언부 규칙 **비대상** (r2 P-C7) | 라우팅 조회 서비스이며 네트워크·메모리 프리미티브가 아님. 주석은 `<remarks>` 로 충분 |
| `WebApplicationFactory<Program> _factory` 선언 | 인라인 근거 주석 | 인메모리 TestServer 호스팅으로 커널 소켓·TCP 핸드셰이크 없이 라우팅·미들웨어·직렬화 전 경로 검증 |
| `HttpClient client` 선언 | 인라인 근거 주석 | `HttpMessageHandler` 가 TestServer 파이프라인에 직결되어 네트워크 스택·포트 점유 없음 |
| `JsonDocument` 선언 | 인라인 근거 주석 (P-C4) | UTF-8 원문을 `ArrayPool<byte>` 대여 버퍼에 보관하므로 `using` 으로 반환해야 풀 누수·GC 압력이 없음 |
| `/health` 핸들러 | 선언부 규칙 비대상(네트워크·메모리 타입 선언 없음) | `DateTimeOffset.UtcNow` 근거 1줄만 |

런타임: 공유 상태 없음, 요청마다 새 record, 락 없음.

## 5. 예외·실패 경로

- 입력·외부 I/O 가 없어 **도메인 실패 경로는 없다.** 직렬화·전송 오류는 ASP.NET Core 기본 처리에 위임하며 `try/catch`·`ProblemDetails`·503 분기를 만들지 않는다 (P-X9).
- `UseHttpsRedirection()`: TestServer 에는 https 포트 정보(`HTTPS_PORT`/`ASPNETCORE_HTTPS_PORT`/서버 주소 피처)가 없어 경고 후 통과. 기본 `CreateClient()` 사용 (P-X5 기각·P-C1). 구현 중 307 이 실제 관측되면 미들웨어를 옮기지 않고 **`_factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") })` 로 요청해 200·본문을 검증**하고 사유·관측 로그를 `20_impl_notes.md` 에 기록한다(r2 X-F2). `AllowAutoRedirect=false` 는 307 을 그대로 돌려주므로 진단 용도로만 쓴다.
- `WithName` 중복 시 시작 예외 → 통합 테스트가 즉시 실패해 포착.
- 시각 창 단정은 동일 프로세스·동일 시계 조건에서 안정적이며, 시스템 시계 보정·역행에는 영향받을 수 있다. 재시도·`TimeProvider` 도입 없음 (P-X7).

## 6. 테스트 전략 — `WebProject.Api.Tests/HealthEndpointTests.cs`

공통: `namespace WebProject.Api.Tests;`, `public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>`, 생성자 주입 `_factory`. 프로덕션 타입 `HealthResponse` 를 참조하지 않는다.

using 지시문(ImplicitUsings·`<Using Include="Xunit" />` 가 커버하지 않음, r2 P-C8): `System.Net`(HttpStatusCode), `System.Text.Json`(JsonDocument·JsonValueKind), `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.AspNetCore.Routing`(LinkGenerator·GetPathByName 확장), `Microsoft.Extensions.DependencyInjection`(GetRequiredService).

**Fact 1 — `GetHealth_Returns200WithHealthyStatusAndUtcTimestamp`** (`public async Task`)
1. `var startedAt = DateTimeOffset.UtcNow;`
2. `using var client = _factory.CreateClient();` → `using var response = await client.GetAsync("/health");`
3. `Assert.Equal(HttpStatusCode.OK, response.StatusCode)`; `Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType)` (P-C5).
4. `using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());` 루트가 `JsonValueKind.Object`.
5. `doc.RootElement.TryGetProperty("status", out var status)` 참, `status.ValueKind == String`, `status.GetString() == "Healthy"` (정확한 키·리터럴 값, P-X2).
6. `TryGetProperty("generatedAt", out var gen)` 참, `gen.ValueKind == String`; 원시 문자열 `gen.GetString()` 이 `"Z"` 또는 `"+00:00"` 으로 끝나는지(`Assert.True(raw.EndsWith("Z") || raw.EndsWith("+00:00"))`, r2 X-F1 — 오프셋 부재를 잡기 위함); `gen.TryGetDateTimeOffset(out var generatedAt)` 참(ISO 8601 파싱 가능).
7. `Assert.Equal(TimeSpan.Zero, generatedAt.Offset)`.
8. `var finishedAt = DateTimeOffset.UtcNow; Assert.InRange(generatedAt, startedAt, finishedAt);` (양끝 포함).

**Fact 2 — `GetHealth_IsNamedGetHealth`** (필수, P-X6·P-C2; **동기 `public void`**, r2 X-F3)
- `var linkGenerator = _factory.Services.GetRequiredService<LinkGenerator>();`
- `Assert.Equal("/health", linkGenerator.GetPathByName("GetHealth", values: null));` — CS0121(오버로드 모호) 시 `(object?)null` 로 캐스팅(동작 동일, r2 P-C8).
- `LinkGenerator` 가 `IEndpointNameMetadata` 로 엔드포인트를 찾으므로 이름 삭제·오타 시 `null` 이 반환돼 실패한다. 이 경로가 이 SDK 에서 실패하면(예: 호스트 미기동) 대안으로 `EndpointDataSource.Endpoints` 를 열거해 `RouteEndpoint.RoutePattern.RawText == "/health"` 인 엔드포인트의 `Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetHealth"` 를 단정하고 사유를 `20_impl_notes.md` 에 적는다.

기존 테스트 3파일은 한 글자도 수정하지 않는다.

## 7. 구현·검증 순서 (P-X1 반영)

1. 시작 시점 `git status --short` 기록(기존 미추적 `_workspace/` 는 제외 대상).
2. `WebProject.Api/Program.cs` — `HealthResponse` record 추가 + `/health` `MapGet` 삽입.
3. `WebProject.Api.Tests/HealthEndpointTests.cs` 작성(주석 규칙 §4 포함).
4. `dotnet build WebProject.sln -c Release` — 오류 0, **기준 커밋 대비 신규 경고 0**(필요 시 base_sha 빌드 경고를 베이스라인으로 비교. 두 csproj 모두 `GenerateDocumentationFile` 미설정이라 CS1591 없음).
5. `dotnet test WebProject.sln -c Release --no-build --filter "FullyQualifiedName~HealthEndpointTests"` — **실행된 테스트 2개·통과 2개** 확인.
6. `dotnet test WebProject.sln -c Release --no-build` — 전체 통과(회귀 0). 결과 원문을 `20_test_results.txt` 에 보존.
7. 종료 시점 `git status --short` 를 1 과 비교 — 제품·테스트 변경은 `M WebProject.Api/Program.cs`, `?? WebProject.Api.Tests/HealthEndpointTests.cs` 만이고 그 외는 `_workspace/cross/<run>/**` 산출물뿐(P-X10·r2 X-F4). `git diff -- WebProject.Api/Program.cs` 로 기존 줄 무변경(추가만) 확인, `git diff --no-index -- /dev/null WebProject.Api.Tests/HealthEndpointTests.cs` 로 신규 파일 본문 확인.
8. 커밋하지 않는다(Stop 훅 담당). `20_impl_notes.md` 에 결정·이탈·관측 사항 기록.

## 8. 비범위 (non-goals)

컨텍스트 비범위(HealthChecks 패키지·DB/외부 점검·인증·레이트 리밋·OpenAPI 커스터마이징·`/weatherforecast` 변경) + `.Produces<>()`, `TypedResults`, `IHealthProvider`/DI 추상화, 캐싱·압축·로깅·503, `TimeProvider` 주입, `MapGroup`, 기존 코드 리팩토링, 공개 상수, `plan/` 문서, 패키지 추가, 커밋.

## 9. 승계 미해결(비차단)

- Production 에서 `/health` 가 `UseHttpsRedirection()` 뒤에 있어 **HTTPS 포트가 결정되는 환경에서는** 평문 접근 시 307. 요구사항 밖이므로 동일 파이프라인 유지. `90_final_report.md` 에 승계.
- 기존 테스트 파일(생성자·Fact 무주석)과 신규 파일의 문서화 수준 차이 — 기존 무변경 원칙 우선, 일괄 정비는 후속 과제.

## 지시
1. 조정 결과에서 "채택"된 의견이 통합 계획에 실제로 반영되었는지 항목별로 대조하라.
2. 통합 과정에서 새로 생긴 모순·누락이 있는지 확인하라.
3. 남은 미해결 항목 중 구현을 막을 만큼 중대한 것이 있는지 판정하라.

마지막 줄에 반드시 `VERDICT: APPROVE` 또는 `VERDICT: REQUEST-CHANGES`를 출력하고, REQUEST-CHANGES면 바로 위에 사유를 번호 목록으로 기술하라. 한국어로 작성하고 파일을 수정하지 마라.
