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
