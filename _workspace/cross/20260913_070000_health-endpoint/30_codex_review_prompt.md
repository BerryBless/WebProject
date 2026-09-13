너는 이 리포지토리의 구현 결과를 독립 리뷰하는 역할이다. 다른 모델의 리뷰가 별도로 진행 중이며 서로 교차 검증할 예정이다. 네 판단만 작성하라. **`_workspace/cross/` 아래의 다른 파일(특히 `*claude*`)은 읽지 마라** — 읽으면 이 리뷰는 독립성 위반으로 무효 처리된다. 아래에 지정된 diff 파일과 프로젝트 소스만 참조하라.
또한 `_workspace/` 디렉터리는 목록 조회(ls)도 하지 말고 어떤 파일도 열지 마라 — 필요한 diff 전문과 테스트 결과는 아래에 전부 인라인되어 있고, 열어야 할 파일은 리포지토리 소스(`WebProject.Api/`, `WebProject.Api.Tests/`, `AGENTS.md`)뿐이다.

## 확정 계획 (구현이 따라야 했던 기준)
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

## 기준 커밋과 변경 범위
- 기준 커밋: 524f5a730867ce59e226bd4f9332961b8b8df6f8
- 변경 diff (커밋되지 않은 변경·신규 파일 포함): 아래 「변경 diff 전문」 절에 전문이 인라인되어 있다(파일을 찾아 열 필요 없음). 신규 파일 목록: WebProject.Api.Tests/HealthEndpointTests.cs (신규 파일 본문 126줄은 diff 에 전부 포함되어 있다).
- 관련 파일은 리포에서 직접 열어 전체 맥락을 확인하라.

## 변경 diff 전문 (base 524f5a730867ce59e226bd4f9332961b8b8df6f8 → 현재 작업 트리)
```diff
diff --git a/WebProject.Api/Program.cs b/WebProject.Api/Program.cs
index f87eb39..e304543 100644
--- a/WebProject.Api/Program.cs
+++ b/WebProject.Api/Program.cs
@@ -34,6 +34,12 @@ app.MapGet("/weatherforecast", () =>
 })
 .WithName("GetWeatherForecast");
 
+app.MapGet("/health", static () =>
+    // DateTimeOffset.UtcNow: 로컬 타임존 변환(tzdata/레지스트리 조회)을 거치지 않고 시스템 UTC 틱을
+    // 그대로 읽으므로 오프셋 0 이 보장되고 Now 보다 호출 비용이 낮다.
+    new HealthResponse("Healthy", DateTimeOffset.UtcNow))
+    .WithName("GetHealth");
+
 app.Run();
 
 /// <summary>일별 기상 예보 응답 모델.</summary>
@@ -67,5 +73,19 @@ public record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
     public int TemperatureF => 32 + TemperatureC * 9 / 5;
 }
 
+/// <summary>서비스 생존 여부를 알리는 <c>/health</c> 응답 모델.</summary>
+/// <param name="Status">서비스 상태. 현 단계에서는 외부 의존성 점검 없이 상수 <c>"Healthy"</c> 를 반환한다.</param>
+/// <param name="GeneratedAt">서버가 응답을 생성한 UTC 시각(오프셋 0).</param>
+/// <remarks>
+/// <b>[성능 및 동시성 제약 조건]</b>
+/// <list type="bullet">
+/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 record 이며 공유 가변 상태가 없다.</description></item>
+/// <item><description><b>Memory Allocation:</b> 요청당 record 인스턴스 1개 힙 할당. <paramref name="Status"/> 는 상수 문자열 인터닝으로 추가 할당 없음.
+/// JSON 직렬화·응답 전송 버퍼는 프레임워크 소관이다.</description></item>
+/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O·대기 없음.</description></item>
+/// </list>
+/// </remarks>
+public record HealthResponse(string Status, DateTimeOffset GeneratedAt);
+
 // WebApplicationFactory<Program> 기반 통합 테스트가 진입점 타입에 접근할 수 있도록 공개한다.
 public partial class Program { }
diff --git a/WebProject.Api.Tests/HealthEndpointTests.cs b/WebProject.Api.Tests/HealthEndpointTests.cs
new file mode 100644
index 0000000..ea7e710
--- /dev/null
+++ b/WebProject.Api.Tests/HealthEndpointTests.cs
@@ -0,0 +1,126 @@
+using System.Net;
+using System.Text.Json;
+using Microsoft.AspNetCore.Mvc.Testing;
+using Microsoft.AspNetCore.Routing;
+using Microsoft.Extensions.DependencyInjection;
+
+namespace WebProject.Api.Tests;
+
+/// <summary>
+/// <c>/health</c> 최소 API 엔드포인트의 통합 테스트.
+/// </summary>
+/// <remarks>
+/// <b>[성능 및 동시성 제약 조건]</b>
+/// <list type="bullet">
+/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행되며, <see cref="WebApplicationFactory{TEntryPoint}"/>가
+/// 인메모리 TestServer를 구동하므로 실제 소켓 바인딩·TCP 핸드셰이크는 발생하지 않는다.</description></item>
+/// <item><description><b>Memory Policy:</b> 팩토리는 <see cref="IClassFixture{TFixture}"/>로 클래스 단위 1회 생성·공유되어
+/// 테스트마다 호스트를 재구동하는 힙 할당을 피한다. <see cref="JsonDocument"/>는 <c>using</c>으로 대여 버퍼를 풀에 반환한다.</description></item>
+/// <item><description><b>Concurrency:</b> 팩토리와 HttpClient는 Thread-safe이며 병렬 테스트 실행에 안전하다.</description></item>
+/// <item><description><b>Blocking:</b> 멤버마다 다르다. <see cref="GetHealth_Returns200WithHealthyStatusAndUtcTimestamp"/>는
+/// 요청 완료를 <c>await</c>로 비동기 대기하며 스레드를 점유하는 동기 블로킹이 없다.
+/// <see cref="GetHealth_IsNamedGetHealth"/>는 동기 실행이며, 최초 <c>_factory.Services</c> 접근에는
+/// 호스트 초기화 비용·대기가 포함될 수 있다.</description></item>
+/// </list>
+/// </remarks>
+public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
+{
+    // WebApplicationFactory<Program>: Program 진입점을 인메모리 TestServer로 호스팅해 커널 소켓·TCP 핸드셰이크 없이
+    // 요청 파이프라인 전체(라우팅·미들웨어·직렬화)를 검증할 수 있어 통합 테스트 오버헤드가 가장 낮다.
+    private readonly WebApplicationFactory<Program> _factory;
+
+    /// <summary>xUnit이 주입한 클래스 픽스처 팩토리를 보관한다.</summary>
+    /// <param name="factory">클래스 단위로 1회 생성·공유되는 인메모리 호스트 팩토리</param>
+    /// <remarks>
+    /// <b>[성능 및 동시성 제약 조건]</b>
+    /// <list type="bullet">
+    /// <item><description><b>Thread Safety:</b> xUnit이 테스트마다 클래스 인스턴스를 새로 생성하며, 공유되는 상태는
+    /// Thread-safe한 fixture 참조뿐이다.</description></item>
+    /// <item><description><b>Memory Allocation:</b> 필드 대입만 수행하며 추가 할당이 없다.</description></item>
+    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). 호스트 기동은 여기서 일어나지 않는다.</description></item>
+    /// </list>
+    /// </remarks>
+    public HealthEndpointTests(WebApplicationFactory<Program> factory)
+    {
+        _factory = factory;
+    }
+
+    /// <summary>
+    /// <c>GET /health</c>가 200과 <c>{"status":"Healthy","generatedAt":&lt;UTC ISO 8601&gt;}</c>를 반환하고,
+    /// <c>generatedAt</c>이 오프셋 0이며 요청 시작~완료 구간 안의 시각인지 검증한다.
+    /// </summary>
+    /// <remarks>
+    /// <b>[성능 및 동시성 제약 조건]</b>
+    /// <list type="bullet">
+    /// <item><description><b>Thread Safety:</b> 테스트 전용 <c>HttpClient</c>·응답 객체만 사용하므로 다른 테스트와
+    /// 공유하는 가변 상태가 없다.</description></item>
+    /// <item><description><b>Memory Allocation:</b> <c>HttpClient</c>·<c>HttpResponseMessage</c>·<see cref="JsonDocument"/>를
+    /// <c>using</c>으로 Dispose하며, 이때 <see cref="JsonDocument"/>의 대여 버퍼를 풀에 반환한다.
+    /// 응답 문자열 등 관리 객체는 GC 대상이다.</description></item>
+    /// <item><description><b>Blocking:</b> 요청 완료를 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
+    /// </list>
+    /// </remarks>
+    [Fact]
+    public async Task GetHealth_Returns200WithHealthyStatusAndUtcTimestamp()
+    {
+        var startedAt = DateTimeOffset.UtcNow;
+
+        // HttpClient: HttpMessageHandler가 TestServer 파이프라인에 직결되어 네트워크 스택·포트 점유 없이 요청을 전달한다.
+        using var client = _factory.CreateClient();
+
+        // HttpResponseMessage: 응답 콘텐츠 스트림과 내부 버퍼의 소유권을 가지므로 테스트 스코프 종료 시 즉시 반환한다.
+        using var response = await client.GetAsync("/health");
+
+        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
+        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
+
+        // JsonDocument: UTF-8 원문을 ArrayPool<byte> 대여 버퍼에 보관하므로 using으로 반환해야 풀 누수·GC 압력이 없다.
+        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
+        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
+
+        Assert.True(doc.RootElement.TryGetProperty("status", out var status), "응답에 status 속성이 없습니다.");
+        Assert.Equal(JsonValueKind.String, status.ValueKind);
+        Assert.Equal("Healthy", status.GetString());
+
+        Assert.True(doc.RootElement.TryGetProperty("generatedAt", out var gen), "응답에 generatedAt 속성이 없습니다.");
+        Assert.Equal(JsonValueKind.String, gen.ValueKind);
+
+        var raw = gen.GetString();
+        Assert.NotNull(raw);
+        // 오프셋 표기가 없으면 System.Text.Json이 로컬 시각으로 해석해 UTC 환경에서 결함이 숨으므로
+        // 파싱 전에 원시 문자열에서 오프셋 표기 존재를 먼저 확인한다.
+        Assert.True(
+            raw.EndsWith("Z", StringComparison.Ordinal) || raw.EndsWith("+00:00", StringComparison.Ordinal),
+            $"generatedAt에 UTC 오프셋 표기가 없습니다: {raw}");
+
+        Assert.True(gen.TryGetDateTimeOffset(out var generatedAt), $"generatedAt을 ISO 8601로 파싱할 수 없습니다: {raw}");
+        Assert.Equal(TimeSpan.Zero, generatedAt.Offset);
+
+        var finishedAt = DateTimeOffset.UtcNow;
+        // InRange는 양끝 포함이므로 요청 시작·완료 시각과 동일한 순간도 허용된다.
+        Assert.InRange(generatedAt, startedAt, finishedAt);
+    }
+
+    /// <summary>
+    /// <c>/health</c> 엔드포인트가 <c>GetHealth</c> 이름으로 등록되어 라우팅 이름으로 경로를 역생성할 수 있는지 검증한다.
+    /// </summary>
+    /// <remarks>
+    /// <b>[성능 및 동시성 제약 조건]</b>
+    /// <list type="bullet">
+    /// <item><description><b>Thread Safety:</b> DI 컨테이너에서 싱글턴 <see cref="LinkGenerator"/>를 조회해 읽기만 하며,
+    /// 해당 서비스는 Thread-safe하다.</description></item>
+    /// <item><description><b>Memory Allocation:</b> 경로 문자열 등 조회 결과만 할당된다.
+    /// 최초 호출 시 발생하는 호스트 초기화 할당은 fixture 소관이다.</description></item>
+    /// <item><description><b>Blocking:</b> 동기 실행. 최초 <c>_factory.Services</c> 접근에는 호스트 초기화 비용·대기가
+    /// 포함될 수 있다.</description></item>
+    /// </list>
+    /// </remarks>
+    [Fact]
+    public void GetHealth_IsNamedGetHealth()
+    {
+        var linkGenerator = _factory.Services.GetRequiredService<LinkGenerator>();
+
+        // LinkGenerator는 IEndpointNameMetadata로 엔드포인트를 찾으므로 이름이 삭제·변경되면 null이 반환돼 실패한다.
+        Assert.Equal("/health", linkGenerator.GetPathByName("GetHealth", values: null));
+    }
+}
```

## 테스트 실행 결과 (구현자 제공)
```text
# 20_test_results.txt — /health 엔드포인트 최초 구현 검증 결과
run: 20260913_070000_health-endpoint / base_sha: 524f5a730867ce59e226bd4f9332961b8b8df6f8
실행 위치: E:\project\WebProject (모든 명령 동일)
실행 일자: 2026-09-13 15:53:30 +0900

요약
  게이트 1 빌드(기준 커밋 상태, 베이스라인): 오류 0 / 경고 0
  게이트 2 빌드(구현 후): 오류 0 / 경고 0  → 신규 경고 0 (게이트 충족)
  게이트 3 필터 테스트(HealthEndpointTests): 전체 2 / 통과 2 / 실패 0 / 건너뜀 0
  게이트 4 전체 테스트: 전체 11 / 통과 11 / 실패 0 / 건너뜀 0 (회귀 0)
  실패 테스트: 없음 (실패 메시지 원문 없음)
  시도 횟수: 1 (재시도 없음). 307 미관측 → 계획 §5 HTTPS 폴백 미적용. LinkGenerator 경로 성공 → 계획 §6 대안 미적용.

===================================================================
[0] 베이스라인 빌드 — 코드 변경 전(작업 트리 깨끗, _workspace 만 미추적)
명령: dotnet build WebProject.sln -c Release   (아래는 콘솔 출력 전사 — tee 캡처가 아니라 관측 그대로 옮긴 것이며, 증분 빌드 특성상 복원 문구·경과 시간은 재실행 시 달라질 수 있다)
-------------------------------------------------------------------
  복원할 프로젝트를 확인하는 중...
  E:\project\WebProject\WebProject.Sample\WebProject.Sample.csproj을(를) 84밀리초 동안 복원했습니다.
  E:\project\WebProject\WebProject.Api\WebProject.Api.csproj을(를) 900밀리초 동안 복원했습니다.
  E:\project\WebProject\WebProject.Api.Tests\WebProject.Api.Tests.csproj을(를) 912밀리초 동안 복원했습니다.
  WebProject.Sample -> E:\project\WebProject\WebProject.Sample\bin\Release\net10.0\WebProject.Sample.dll
  WebProject.Api -> E:\project\WebProject\WebProject.Api\bin\Release\net10.0\WebProject.Api.dll
  WebProject.Api.Tests -> E:\project\WebProject\WebProject.Api.Tests\bin\Release\net10.0\WebProject.Api.Tests.dll

빌드했습니다.
    경고 0개
    오류 0개

경과 시간: 00:00:04.03

===================================================================
[1] 구현 후 빌드
명령: dotnet build WebProject.sln -c Release   (아래는 콘솔 출력 전사 — tee 캡처가 아니라 관측 그대로 옮긴 것이며, 증분 빌드 특성상 복원 문구·경과 시간은 재실행 시 달라질 수 있다)
-------------------------------------------------------------------
  복원할 프로젝트를 확인하는 중...
  복원할 모든 프로젝트가 최신 상태입니다.
  WebProject.Sample -> E:\project\WebProject\WebProject.Sample\bin\Release\net10.0\WebProject.Sample.dll
  WebProject.Api -> E:\project\WebProject\WebProject.Api\bin\Release\net10.0\WebProject.Api.dll
  WebProject.Api.Tests -> E:\project\WebProject\WebProject.Api.Tests\bin\Release\net10.0\WebProject.Api.Tests.dll

빌드했습니다.
    경고 0개
    오류 0개

경과 시간: 00:00:01.40
판정: 베이스라인과 동일하게 경고 0 → 신규 파일·삽입 코드에서 기인한 경고 없음.
      (CS8602 미발생 → Assert.NotNull(raw) 이후 null 역참조 경고 없음. CS0121 미발생 → GetPathByName 오버로드 모호성 없음.)

===================================================================
[2] 필터 테스트
 명령: dotnet test WebProject.sln -c Release --no-build --filter "FullyQualifiedName~HealthEndpointTests"
종료 코드: 0
-------------------------------------------------------------------
E:\project\WebProject\WebProject.Api.Tests\bin\Release\net10.0\WebProject.Api.Tests.dll(.NETCoreApp,Version=v10.0)에 대한 테스트 실행
지정된 패턴과 일치한 총 테스트 파일 수는 1개입니다.

통과!  - 실패:     0, 통과:     2, 건너뜀:     0, 전체:     2, 기간: 1 s - WebProject.Api.Tests.dll (net10.0)

===================================================================
[3] 전체 테스트
명령: dotnet test WebProject.sln -c Release --no-build
종료 코드: 0
-------------------------------------------------------------------
E:\project\WebProject\WebProject.Api.Tests\bin\Release\net10.0\WebProject.Api.Tests.dll(.NETCoreApp,Version=v10.0)에 대한 테스트 실행
지정된 패턴과 일치한 총 테스트 파일 수는 1개입니다.

통과!  - 실패:     0, 통과:    11, 건너뜀:     0, 전체:    11, 기간: 178 ms - WebProject.Api.Tests.dll (net10.0)

===================================================================
[4] 변경 범위 확인 (계획 §7.1 / §7.7)
명령: git status --short --untracked-files=all -- . ':(exclude)_workspace'
-------------------------------------------------------------------
시작 시점: (출력 없음 — 제품·테스트 트리 깨끗)
종료 시점:
 M WebProject.Api/Program.cs
?? WebProject.Api.Tests/HealthEndpointTests.cs

명령: git diff --numstat -- WebProject.Api/Program.cs
  20      0       WebProject.Api/Program.cs      → 삽입 20줄, 삭제 0줄(기존 줄 무변경)
명령: git diff --no-index --numstat -- /dev/null WebProject.Api.Tests/HealthEndpointTests.cs
  126     0       nul => WebProject.Api.Tests/HealthEndpointTests.cs  → 신규 126줄
```

## 지시
다음 축으로 리뷰하라: 버그(정확성), 회귀 가능성, 보안, 성능(핫패스 할당·블로킹), 요구사항 충족(확정 계획 대비), 테스트 누락, **프로젝트 규칙 준수**(AGENTS.md: public API XML `<remarks>` 의 Thread Safety·Memory Allocation·Blocking 3항목, 메모리·네트워크 타입 선언부의 내부 동작 근거 주석 — 누락은 Med).

각 지적은 반드시 이 형식으로: `[R-X#] 심각도(High/Med/Low) | 파일:라인 | 발생 조건 | 영향 | 근거 | 수정 방향`

- 코드 근거 없는 지적은 내지 마라. 동시성 주장은 재현 조건을 구체적으로 기술하라.
- 스타일 선호는 [취향]으로 분리하고 심각도를 매기지 마라.
- 계획에 없는 변경(범위 이탈)이 diff에 있으면 별도 섹션으로 지적하라.

마지막에 `## 종합`: High 지적 수, 병합 가능 여부에 대한 의견. 그리고 **마지막 줄에 `VERDICT: APPROVE`(High 0건) 또는 `VERDICT: REQUEST-CHANGES`** 를 출력하라. 한국어로 작성하고 파일을 수정하지 마라.
