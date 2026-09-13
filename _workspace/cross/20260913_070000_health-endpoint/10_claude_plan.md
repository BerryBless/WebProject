# Claude 독립 구현 계획 — `GET /health` 엔드포인트

- run: `_workspace/cross/20260913_070000_health-endpoint`
- base_sha: `524f5a730867ce59e226bd4f9332961b8b8df6f8`
- 입력: `00_context.md` + 프로젝트 소스 + `CLAUDE.md` (Codex 산출물 미참조 — 독립성 준수)

---

## 1. 설계 결정

### 1.1 응답 모델 배치 (Program.cs vs 별도 파일)

| 후보 | 장점 | 단점 | 판정 |
|------|------|------|------|
| `Program.cs` 하단에 record 추가 | 기존 `WeatherForecast` 와 같은 자리, 파일 수 불변 | 진입점 파일이 계속 비대해짐. 제약 "`Program.cs` 최소 변경" 과 상충 | 대안 |
| **`WebProject.Api/HealthResponse.cs` 신규** | 진입점 diff 가 `MapGet` 블록 1개로 축소됨(제약 직접 충족). 공개 계약 타입 1개 = 파일 1개(표준 C# 관례) | 파일 1개 추가, `WeatherForecast` 와 배치가 달라짐 | **채택** |

- 네임스페이스는 **선언하지 않는다(전역 네임스페이스)**. `Program.cs` 의 `WeatherForecast` 가 전역 네임스페이스에 있어 `namespace WebProject.Api;` 를 붙이면 같은 어셈블리 안에서 배치 규칙이 갈린다.
- 테스트는 프로덕션 타입을 참조하지 않고 자체 DTO record 를 정의한다(기존 `WeatherForecastEndpointTests.WeatherForecastDto` 와 동일 패턴). 따라서 네임스페이스 접근성은 배치 결정의 제약이 아니다 — 위 표의 근거에서 제외했다.

### 1.2 직렬화 형태 — `+00:00` vs `Z` (중요)

`DateTimeOffset` 을 System.Text.Json 이 직렬화하면 ISO 8601-1:2019 확장 프로파일의 **오프셋 표기**를 쓴다: 예) `"2026-09-13T07:00:00.1234567+00:00"`(후행 0 은 생략되므로 소수부 자릿수는 값에 따라 달라지고, 0 이면 `"2026-09-13T07:00:00+00:00"` 형태가 된다 — 자릿수에 의존하는 단정은 금물). UTC 라도 **`Z` 접미사가 아니다**(`Z` 는 `DateTime` + `Kind=Utc` 일 때의 표기). 요구사항은 `DateTimeOffset.UtcNow` 를 명시하므로 `+00:00` 이 정상 산출물이며 "UTC 시각, ISO 8601" 을 만족한다.

**결정:** 테스트는 원시 JSON 문자열의 접미사(`"Z"`, `"+00:00"`)를 단정하지 않는다. 응답을 `DateTimeOffset` 으로 역직렬화한 뒤 `Offset == TimeSpan.Zero` 로 UTC 를 검증한다. 문자열 단정은 표기 방식이 바뀌면 깨지는 취약한 계약이고, 요구사항이 요구하는 것은 "오프셋 0" 이다.

### 1.3 핸들러 스타일

기존 `/weatherforecast` 와 동일하게 **람다에서 record 를 직접 반환**한다(`TypedResults.Ok(...)` 미사용). 최소 API 는 반환 타입에서 200 + `application/json` 을 추론하므로 동작·OpenAPI 결과가 같고 파일 내 스타일이 일관된다.

---

## 2. 변경 파일 목록과 요지

| # | 파일 | 종류 | 요지 |
|---|------|------|------|
| 1 | `WebProject.Api/HealthResponse.cs` | 신규 | 공개 `record HealthResponse(string Status, DateTimeOffset GeneratedAt)`. 전역 네임스페이스. XML 문서 주석 + `<remarks>`(Thread Safety / Memory Allocation / Blocking). `public const string HealthyStatus = "Healthy";` 상수를 두어 핸들러가 문자열 리터럴을 중복하지 않게 한다. |
| 2 | `WebProject.Api/Program.cs` | 수정 | `app.MapGet("/weatherforecast", ...)` 블록 **뒤**, `app.Run();` **앞**에 `/health` `MapGet` 추가 + `.WithName("GetHealth")`. 그 외 기존 코드 무변경. |
| 3 | `WebProject.Api.Tests/HealthEndpointTests.cs` | 신규 | `IClassFixture<WebApplicationFactory<Program>>` 통합 테스트. 클래스 `<remarks>` + `WebApplicationFactory<Program>`·`HttpClient` 선언부 인라인 근거 주석. 자체 `private sealed record HealthResponseDto(string Status, DateTimeOffset GeneratedAt)`. |

### 2.1 `Program.cs` 삽입 코드 (요지)

```csharp
app.MapGet("/health", () =>
    // DateTimeOffset.UtcNow: 로컬 타임존 변환(레지스트리/tzdata 조회)을 거치지 않고
    // 시스템 UTC 틱을 그대로 읽으므로 오프셋 0 이 보장되고 호출 비용도 더 낮다.
    new HealthResponse(HealthResponse.HealthyStatus, DateTimeOffset.UtcNow))
    .WithName("GetHealth");
```

JSON 직렬화기의 기본 정책(`JsonSerializerDefaults.Web`)이 camelCase 이므로 `Status` → `status`, `GeneratedAt` → `generatedAt` 로 나간다. 별도 `JsonPropertyName` 속성은 불필요하다.

### 2.2 변경하지 않는 것

`WebProject.Api.csproj`, `WebProject.Api.Tests.csproj`(패키지 추가 없음), `WebProject.sln`, `appsettings*.json`, `WeatherForecastEndpointTests.cs`, `WeatherForecastTests.cs`, `WeatherForecastRangeTests.cs`, `WebProject.Sample`, `.github/workflows/ci.yml`.

---

## 3. 공개 API 영향

- **신규 공개 타입:** `HealthResponse`(전역 네임스페이스, `WebProject.Api` 어셈블리). record 라 생성자·`Status`/`GeneratedAt` 속성·`Equals`/`GetHashCode`/`ToString`/`Deconstruct`/`with` 복사 생성자가 자동 공개된다. 공개 표면 확장이므로 XML 문서 주석 규칙 대상.
- **신규 공개 상수:** `HealthResponse.HealthyStatus`.
- **신규 HTTP 계약:** `GET /health` → 200, `application/json`, `{ "status": "Healthy", "generatedAt": "<ISO 8601, offset +00:00>" }`. 엔드포인트 이름 `GetHealth`(라우트 이름은 전역 유일해야 하며 기존은 `GetWeatherForecast` 하나뿐이라 충돌 없음).
- **OpenAPI:** Development 환경에서만 노출되는 `MapOpenApi` 문서에 `/health` 가 자동 포함된다. 스키마 커스터마이징(`.Produces<HealthResponse>(200)`, `.WithSummary` 등)은 **하지 않는다** — 반환 타입으로 이미 추론되고, 비범위인 "OpenAPI 스키마 커스터마이징" 에 접한다.
- **기존 계약 파괴 없음:** `/weatherforecast` 경로·응답·이름 불변.

---

## 4. 동시성·메모리 정책 (CLAUDE.md 규칙 적용 범위)

### 4.1 `<remarks>` 규칙 (Thread Safety / Memory Allocation / Blocking) — **대상**

| 대상 | 적용 | 기술할 내용 |
|------|------|-------------|
| `HealthResponse` (public record) | 필수 | **Thread Safety:** Thread-safe. 불변 record 이고 가변 상태·공유 필드가 없다. **Memory Allocation:** 인스턴스 1개(참조 타입 record) 힙 할당 + 직렬화 버퍼. `Status` 는 상수 문자열 인터닝이라 요청당 문자열 할당은 없다. **Blocking:** 즉시 반환(Non-blocking). |
| `HealthResponse.HealthyStatus` (public const) | 필수(summary) | 응답 계약 상수임을 명시. |
| `HealthEndpointTests` (public sealed class) | 필수 | 기존 `WeatherForecastEndpointTests` 와 동일 축(Thread Context / Memory Policy / Concurrency)으로 작성. |

### 4.2 선언부 인라인 `//` 주석 규칙 (네트워크·메모리 타입) — **부분 대상**

| 선언 | 적용 | 근거로 쓸 내부 동작 |
|------|------|--------------------|
| 테스트의 `WebApplicationFactory<Program> _factory` | 대상 | 진입점을 인메모리 `TestServer` 로 호스팅해 커널 소켓 바인딩·TCP 핸드셰이크 없이 라우팅·직렬화 전 경로를 태운다. `IClassFixture` 로 클래스당 1회 생성되어 호스트 재구동 할당을 피한다. |
| 테스트의 `HttpClient client` | 대상 | 팩토리가 만든 클라이언트는 `HttpMessageHandler` 가 `TestServer` 파이프라인에 직결되어 네트워크 스택·포트 점유 없이 요청이 전달된다. |
| `Program.cs` 의 `/health` 핸들러 | **비대상(명시)** | 이 변경에는 `Socket`/`Pipe`/`Channel<T>`/`ArrayPool<T>`/`Memory<T>` 등 네트워크·메모리 타입 **선언이 없다**(람다 하나와 record 생성뿐). 규칙이 요구하는 선언부 주석 대상이 존재하지 않으므로 억지 주석을 넣지 않는다. 대신 `DateTimeOffset.UtcNow` 선택 근거만 1줄 인라인으로 남긴다(§2.1). |
| `HealthResponse` 의 속성 | 비대상 | `string`/`DateTimeOffset` 은 규칙이 나열한 네트워크·메모리 타입군이 아니다. `<remarks>` 로 충분. |

### 4.3 런타임 동시성 실제

핸들러는 상태를 공유하지 않고 요청마다 새 record 를 만들며, `DateTimeOffset.UtcNow` 는 락 없는 시스템 시계 읽기다. 동시 요청 간 경합·가시성 문제가 발생할 지점이 없다.

---

## 5. 예외·실패 경로

**정직한 결론: 런타임 예외 경로가 없다.** 근거 — 입력 파라미터 없음(검증 대상 없음), 외부 I/O·DB·파일 접근 없음(비범위), 상수 문자열과 시계 읽기만 수행, 할당 실패 외에 `DateTimeOffset.UtcNow` 는 throw 하지 않는다. 따라서 `try/catch`·`ProblemDetails`·커스텀 예외 타입을 **도입하지 않는다**(도입하면 과도한 설계).

그럼에도 계획 단계에서 확인해 둘 실패 지점:

| 지점 | 위험 | 대응 |
|------|------|------|
| `UseHttpsRedirection()` 이 테스트에서 307 을 유발 | 낮음 | `WebApplicationFactory` 는 `HTTPS_PORT`/`ASPNETCORE_HTTPS_PORT` 를 설정하지 않아 미들웨어가 경고 로그만 남기고 통과(pass-through)한다. 반증: 동일 파이프라인을 지나는 기존 `WeatherForecastEndpointTests` 가 200 으로 통과 중. 만약 실제로 307 이 관측되면 미들웨어 순서를 바꾸지 말고 테스트 측 `AllowAutoRedirect` 로 대응한다. |
| 라우트 이름 중복(`WithName`) | 없음 | 기존 이름은 `GetWeatherForecast` 뿐. 중복 시 시작 시점 `InvalidOperationException` 이므로 통합 테스트가 즉시 실패해 잡힌다. |
| `generatedAt` 시각 창(window) 단정의 플래키 | 낮음 | §6.1 의 시각 창 단정 근거 참조. |
| Production 환경에서 OpenAPI 미노출 | 해당 없음 | `/health` 자체는 환경 무관하게 매핑된다. `MapOpenApi` 만 Development 조건부. |

---

## 6. 테스트 전략

### 6.1 신규: `WebProject.Api.Tests/HealthEndpointTests.cs`

`WeatherForecastEndpointTests` 의 구조를 그대로 따른다(`IClassFixture<WebApplicationFactory<Program>>`, `CreateClient()`, `ReadFromJsonAsync<T>`, 자체 DTO record).

**Fact 1 — `GetHealth_Returns200WithHealthyStatusAndUtcTimestamp` (요구사항 필수 3항목 전부)**

```
var before = DateTimeOffset.UtcNow;          // 요청 전 시각 고정
var response = await client.GetAsync("/health");
var after = DateTimeOffset.UtcNow;           // 요청 후 시각 고정

Assert.Equal(HttpStatusCode.OK, response.StatusCode);
var body = await response.Content.ReadFromJsonAsync<HealthResponseDto>();
Assert.NotNull(body);
Assert.Equal("Healthy", body.Status);                  // 계약 문자열을 리터럴로 단정(상수 자기참조 회피)
Assert.Equal(TimeSpan.Zero, body.GeneratedAt.Offset);  // UTC 검증 — 문자열 접미사에 의존하지 않음
Assert.InRange(body.GeneratedAt, before, after);       // 양끝 포함
```

- `Status` 는 `HealthResponse.HealthyStatus` 가 아니라 **리터럴 `"Healthy"`** 로 단정한다. 상수를 쓰면 상수를 바꿔도 테스트가 통과해 계약 회귀를 못 잡는다.
- **시각 창 단정이 플래키하지 않은 이유:** `before`/`after` 와 서버가 같은 프로세스·같은 시스템 시계를 읽고, `before` 는 요청 발신보다 엄격히 먼저 실행된다. `Assert.InRange` 는 양끝 포함이라 시계 해상도가 거칠어도(동일 틱 반환) 실패하지 않는다. 직렬화 왕복은 무손실이라(STJ 의 ISO 8601 표기는 필요한 최소 자릿수만 쓰고 후행 0 만 생략하므로 틱이 그대로 복원된다) 절삭으로 창을 벗어날 수 없다.

**Fact 2 (선택) — `GetHealth_ReturnsJsonContentType`:** `response.Content.Headers.ContentType?.MediaType == "application/json"`. 요구사항 필수는 아니나 계약 고정 비용이 1줄이다.

### 6.2 기존 테스트

`WeatherForecastEndpointTests`·`WeatherForecastTests`·`WeatherForecastRangeTests` 는 **한 글자도 수정하지 않는다.** 회귀 검증은 솔루션 전체 `dotnet test` 통과로 확인한다.

### 6.3 실행 명령 (프로젝트 루트에서)

```
dotnet build WebProject.sln --configuration Release        # 오류 0 · 기준 커밋 대비 신규 경고 0 확인
dotnet test  WebProject.sln --configuration Release --no-build
dotnet test  WebProject.sln --configuration Release --no-build --filter "FullyQualifiedName~HealthEndpointTests"   # 신규만 표적 실행
```

CI 등가(`.github/workflows/ci.yml`): `dotnet restore` → `dotnet build --configuration Release --no-restore` → `dotnet test --configuration Release --no-build --verbosity normal`.

**성공 기준:** 신규 테스트 전부 통과 + 기존 테스트 전부 통과(회귀 0) + 빌드 오류 0 + **기준 커밋(base_sha) 대비 신규 경고 0**.

경고 기준을 "0" 이 아니라 "신규 0" 으로 잡는 이유: 기준 커밋이 경고 없이 빌드된다는 증거가 없다. 선존재 경고가 있으면 무관한 사유로 게이트가 막힌다. 필요하면 구현 전 base_sha 에서 한 번 빌드해 경고 목록을 베이스라인으로 잡는다. 참고로 두 csproj 모두 `GenerateDocumentationFile` 을 켜지 않아 신규 XML 주석이 CS1591 을 유발하지 않는다.

---

## 7. 단계 순서

1. `WebProject.Api/HealthResponse.cs` 작성 (XML 주석·`<remarks>`·`HealthyStatus` 상수 포함).
2. `WebProject.Api/Program.cs` 에 `/health` `MapGet` + `.WithName("GetHealth")` 삽입 (`app.Run();` 앞).
3. `dotnet build WebProject.sln --configuration Release` — 컴파일 오류 0, 기준 커밋 대비 신규 경고 0 확인.
4. `WebProject.Api.Tests/HealthEndpointTests.cs` 작성.
5. `dotnet test WebProject.sln --configuration Release --no-build --filter "FullyQualifiedName~HealthEndpointTests"` — 신규 통과 확인(3 단계 Release 빌드 산출물 재사용).
6. `dotnet test WebProject.sln --configuration Release` — 전체 회귀 0 확인.
7. `git diff --stat` 로 변경 파일이 위 3개뿐인지 확인(기존 테스트 무변경 증빙).
8. 커밋하지 않는다 (Stop 훅 / `commitandpush` 담당).

---

## 8. 비범위 (non-goals) — 명시적으로 하지 않는 것

컨텍스트 §비범위(HealthChecks 패키지, DB/외부 의존성 점검, 인증, 레이트 리밋, OpenAPI 스키마 커스터마이징, `/weatherforecast` 변경)에 더해 다음을 선제 배제한다:

- `Microsoft.Extensions.Diagnostics.HealthChecks` / `AddHealthChecks()` / `MapHealthChecks()` — 요구사항은 상수 응답이며 패키지 추가는 제약 위반.
- `.Produces<HealthResponse>(StatusCodes.Status200OK)` — 반환 타입에서 이미 추론되고 OpenAPI 커스터마이징 비범위에 접한다.
- `TypedResults.Ok(...)` 로의 스타일 전환 — 기존 `/weatherforecast` 와 불일치.
- `IHealthProvider` 등 추상화·DI 등록·인터페이스 도입 — 구현체 1개뿐인 조기 추상화.
- 캐싱(`OutputCache`), 응답 압축, `ILogger` 로깅 추가, 상태 코드 503 경로.
- `ITimeProvider`/`TimeProvider` 주입으로 시계 가짜화 — 통합 테스트가 시각 창 단정으로 충분히 검증하므로 프로덕션 코드에 테스트 전용 이음새를 만들지 않는다.
- `Program.cs` 기존 코드 리팩토링, 라우트 그룹(`MapGroup`) 도입.
- `plan/<기능명>_<MMDD>.md` 설계 문서 작성 — CLAUDE.md 의 플랜 문서화 규칙은 "기능 설계나 아키텍처 결정이 완료되면" 을 조건으로 한다. 이 변경은 아키텍처 결정이 없는 소형 하네스 검증 기능(엔드포인트 1개 + record 1개)이라 해당하지 않는다. 필요 판단이 서면 §9 의 미해결 질문으로 올린다.

---

## 9. [미해결 질문]

1. **`generatedAt` 표기가 `+00:00` 이어도 되는가?** `DateTimeOffset` 이므로 STJ 기본 직렬화는 `"...+00:00"` 이며 `"...Z"` 가 아니다. 소비 측이 `Z` 접미사를 엄격히 요구하는 클라이언트라면 `DateTime`(Kind=Utc)로 바꾸거나 커스텀 컨버터가 필요하다. 요구사항이 `DateTimeOffset.UtcNow` 를 명시했으므로 현 계획은 `+00:00` 을 정답으로 확정했다 — 다르면 회신 필요.
2. **`/health` 가 Production·HTTPS 리다이렉션 뒤에서도 평문 200 이어야 하는가?** 현재 파이프라인은 `UseHttpsRedirection()` 뒤에 매핑되므로 실서버에서 `http://` 접근 시 307 이 난다. 로드밸런서·오케스트레이터 프로브가 평문으로 찌를 계획이면 `/health` 를 리다이렉션 앞에 두거나 예외 처리해야 한다. 현 계획은 요구사항에 언급이 없어 **다른 엔드포인트와 동일 파이프라인**을 유지했다.
3. **응답 record 배치를 `Program.cs` 로 통일하기를 원하는가?** §1.1 에서 "`Program.cs` 최소 변경" 제약을 우선해 별도 파일을 택했으나, `WeatherForecast` 와의 배치 일관성을 더 중시한다면 `Program.cs` 하단으로 옮기는 것도 동등하게 유효하다(구현·테스트 영향 없음).
