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
