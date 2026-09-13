# Claude 검토 — Codex 계획 (`10_codex_plan.md`)

- run: `_workspace/cross/20260913_070000_health-endpoint/`
- base_sha: `524f5a730867ce59e226bd4f9332961b8b8df6f8`
- 입력: `00_context.md`, `10_codex_plan.md`
- 검토 방법: 계획의 모든 `파일:라인` 인용을 원본에서 대조하고, `WebProject.Api/Program.cs`·`WebProject.Api.Tests/*.cs`·양쪽 csproj·`WebProject.sln`·`appsettings*.json`·`launchSettings.json`·`.github/workflows/ci.yml`·`AGENTS.md`를 직접 읽어 검증했다. 읽기 전용 명령만 사용했고 소스는 수정하지 않았다(빌드·테스트 미실행).

## 총평

계획은 컨텍스트의 요구사항(200 / `status` / `generatedAt` UTC·시각 범위 / `WithName("GetHealth")` / 기존 동작 불변)을 모두 덮고, 비범위도 컨텍스트와 일치한다. 응답 record 위치를 `Program.cs`로 정한 근거(`Program.cs:51`의 `WeatherForecast` 선행 사례)도 컨텍스트 28행이 요구한 "선택·근거 제시"를 충족한다. 인용 12건은 사실상 전부 정확했다(아래 검증 완료 항목 참조). **High 없음.** 지적은 Med 2건·Low 3건으로, 모두 계획 문장 수정 수준에서 해소 가능하다.

---

## 지적

### [P-C1] Med | HTTPS 리디렉션 회피 전략은 잘못된 전제 위에 있고, 기존 테스트 스타일과 충돌하며, 보호 효과도 없다

**근거.** 계획 4절(50행)은 *"신규 테스트는 HTTPS 기준 주소와 자동 리디렉션 비활성화를 사용해 최종 응답만 보고 중간 리디렉션을 놓치지 않도록 한다"* 며 `Program.cs:15`(`app.UseHttpsRedirection()`)를 근거로 든다. 그러나 `WebApplicationFactory` 호스트에서는 리디렉션이 애초에 발생하지 않는다.

1. `HttpsRedirectionMiddleware`는 리디렉션 대상 HTTPS 포트를 (a) 구성 키 `https_port` / `ASPNETCORE_HTTPS_PORT` 또는 (b) 서버의 `IServerAddressesFeature`에서 얻는다. 둘 다 없으면 경고만 남기고 `next()`로 통과시킨다.
2. 이 저장소에는 두 경로 모두 없다. `WebProject.Api/appsettings.json`·`appsettings.Development.json`에는 `Logging`·`AllowedHosts`뿐이고 `https_port` 키가 없다. HTTPS 주소가 적힌 `WebProject.Api/Properties/launchSettings.json`은 `dotnet run` 전용 산출물이라 `WebApplicationFactory`가 읽지 않는다. TestServer는 HTTPS 주소를 노출하지 않는다.
3. 결정적 반증: `WebProject.Api.Tests/WeatherForecastEndpointTests.cs:35-39`는 옵션 없이 `_factory.CreateClient()`(기본 BaseAddress `http://localhost/`)로 호출하고 `Assert.Equal(HttpStatusCode.OK, ...)`를 통과시킨다. 리디렉션이 살아 있었다면 307이 되어 이 기존 테스트가 실패했을 것이다.

또한 `AllowAutoRedirect = false`를 쓰려면 `CreateClient(new WebApplicationFactoryClientOptions { ... })` 형태가 되어, 컨텍스트 16행이 정본으로 지정한 기존 테스트 스타일(`WeatherForecastEndpointTests.cs:35`)과 불필요하게 갈라진다. 보호 효과도 없다 — 정말로 리디렉션이 켜졌다면 이 설정은 307을 잡아내는 게 아니라 200 단언을 실패시킬 뿐이라, 기본 클라이언트를 쓸 때와 실패 시점이 같다.

**제안.** 4절의 해당 문장을 삭제하고 `_factory.CreateClient()` 기본값(HTTP BaseAddress, 자동 리디렉션 기본)을 그대로 쓴다고 명시하라. HTTPS 리디렉션은 "테스트 호스트에서 포트 미결정으로 통과되므로 신규 테스트에 영향 없음"이라고 한 줄 기록하면 충분하다(향후 `https_port`가 추가될 때의 회귀 단서로도 남는다).

---

### [P-C2] Med | 엔드포인트 이름 검증 테스트가 변경 파일 목록에 반영되지 않았고, 메타데이터 조회 메커니즘이 미정이다

**근거.** 계획 5절 71행은 *"엔드포인트 메타데이터 테스트에서 `/health`의 `IEndpointNameMetadata.EndpointName == "GetHealth"`를 검증한다"* 고 새 테스트를 추가한다. 그런데 1절 변경 파일 목록(8행)은 `HealthEndpointTests.cs`를 *"응답 코드, JSON 계약, UTC 오프셋과 시각 범위 검증"* 으로만 기술해, 이 테스트가 어느 파일에 들어가는지(같은 클래스인지 신규 파일인지) 계획 안에서 불일치한다. 6절 구현 순서(88행)에는 등장하므로 누락이 아니라 목록 갱신 누락이다.

더 중요한 것은 **메커니즘이 하나도 적혀 있지 않다는 점**이다. `WithName`이 설정한 메타데이터는 HTTP 응답 본문으로 확인할 수 없으므로 테스트 호스트의 DI에서 꺼내야 하는데, 계획은 그 경로를 특정하지 않았다. 경로에 따라 난이도·실패 모드가 달라지므로(엔드포인트 열거 시 라우트 필터링, 서비스 미등록 시 예외 등) 구현자에게 그대로 넘기면 Red 단계에서 방황한다. 읽기 전용 검토라 어느 쪽이 이 SDK에서 동작하는지 실측하지 못했으므로, 계획이 하나를 지정하고 구현 시 실측할 것을 권한다.

**제안.** 1절 표의 `HealthEndpointTests.cs` 설명에 "엔드포인트 이름(`GetHealth`) 메타데이터 검증"을 추가하고, 다음 중 하나를 계획에서 확정하라.
- (권장, 단순) `_factory.Services.GetRequiredService<LinkGenerator>().GetPathByName("GetHealth", values: null)`이 `/health`를 반환하는지 확인. `LinkGenerator`는 라우팅 기본 서비스라 해석이 안정적이고, 이름 미등록 시 `null`이 되어 Red 단계도 자연스럽다.
- (직접적) `EndpointDataSource.Endpoints`를 열거해 `RouteEndpoint.RoutePattern.RawText == "/health"`인 항목의 `Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName`을 비교.

어느 쪽이든 "구현 중 해석 실패 시 다른 쪽으로 대체하고 본 계약 테스트는 유지"라는 폴백을 한 줄 남길 것.

---

### [P-C3] Low | JSON 키가 camelCase가 되는 근거가 계획에 없다 — 요구사항의 핵심 계약인데 암묵 가정으로 남았다

**근거.** 요구사항(컨텍스트 5행)은 응답 키를 `status`·`generatedAt`으로 못박는데, 계획 2절(18행)의 타입은 `HealthResponse(string Status, DateTimeOffset GeneratedAt)`로 PascalCase다. 두 이름을 잇는 것은 최소 API 기본 직렬화 옵션(`JsonSerializerDefaults.Web` → camelCase 정책)뿐인데, 계획은 *"기존 JSON 직렬화 경로에 맡긴다"*(29행)고만 하고 이 메커니즘을 언급하지 않는다. 실제 동작은 계획대로 맞지만, 근거가 적히지 않아 구현자가 `[JsonPropertyName]` 추가/생략을 임의 판단하게 된다. 다행히 5절 4·5단계의 원시 JSON 키 검사가 안전망으로 작동한다.

**제안.** 2절에 "최소 API 기본 직렬화는 웹 기본 옵션이라 PascalCase 속성이 camelCase 키로 나가므로 `[JsonPropertyName]`을 붙이지 않는다"는 한 줄을 추가하고, 이 가정을 검증하는 것이 5절 4단계의 원시 JSON 키 검사임을 명시하라.

---

### [P-C4] Low | `JsonDocument` 선언에 프로젝트 규칙이 요구하는 인라인 근거 주석 계획이 빠졌다

**근거.** 계획 3절(42행)은 `WebApplicationFactory<Program>`·`HttpClient` 선언부 인라인 주석만 약속한다. 그러나 5절 4단계에서 새로 도입하는 `JsonDocument`(44행에서 `using` 해제 대상으로 언급)는 내부적으로 `ArrayPool` 기반 풀 버퍼를 대여해 보유하고 `Dispose` 시 반환하는 타입이라, `AGENTS.md:112`·`:114`의 "네트워크·메모리 관련 모든 타입의 선언" 대상에 들어간다(`:114` 목록은 `ArrayPool<T>` 등을 "등"으로 열어둔다). 규칙 문구가 "반드시"이므로 선언 시 근거 주석이 필요하다.

**제안.** 3절 주석 계획에 `JsonDocument` 선언을 추가하고, 주석 내용을 내부 동작 근거로 쓸 것(예: 풀 버퍼를 대여해 문서 전체를 POCO 매핑 없이 순회하므로 원시 키·타입을 그대로 검사할 수 있으며, 그래서 `using`으로 풀 반환을 보장한다). 단순 기능 설명은 규칙 위반이다.

---

### [P-C5] Low | 콘텐츠 타입 단언 방식이 미정이라 `charset` 때문에 헛도는 실패가 나올 수 있다

**근거.** 5절 3단계는 *"HTTP 200과 JSON 콘텐츠 유형 확인"*이라고만 적었다. 최소 API의 JSON 응답 헤더는 `application/json; charset=utf-8`이므로 `Assert.Equal("application/json", response.Content.Headers.ContentType?.ToString())` 같은 전체 문자열 비교로 구현하면 기능은 정상인데 테스트만 실패한다. Red→Green 사이클에서 원인 추적 비용만 발생하는 종류의 실패다.

**제안.** 3단계를 `response.Content.Headers.ContentType?.MediaType`과 `"application/json"` 비교로 구체화하라(charset은 단언하지 않음).

---

## [취향]

- **[취향]** 최종 회귀 검증 명령을 CI(`.github/workflows/ci.yml:26-29`)와 맞춰 `dotnet build -c Release` + `dotnet test -c Release --no-build`로 한 번 더 돌리면 CI와 동일 조건이 된다. 다만 두 csproj 어디에도 `TreatWarningsAsErrors`가 없어 구성 차이로 결과가 갈릴 여지는 사실상 없으므로 취향으로 분류한다.
- **[취향]** 5절 6단계의 *"문자열에 `Z` 또는 `+00:00`이 명시되었는지"*는 OR 조건이라 안전하다. 실제로 `DateTimeOffset.UtcNow`는 `+00:00`으로 직렬화되고 `Z`는 나오지 않으므로, 단언을 `+00:00` 단독으로 좁혀 계약을 더 날카롭게 만들 수도 있다. 지금 형태도 틀리지 않는다.
- **[취향]** 이 저장소의 관례는 계약 상수를 응답 record에 두고 테스트가 그것을 참조하는 것이다(`WeatherForecast.ForecastDays` — `Program.cs:54` 선언, `WeatherForecastEndpointTests.cs:43` 소비 / `MinTemperatureC`·`MaxTemperatureC` — `WeatherForecastRangeTests.cs:30`). 계획은 `"Healthy"` 리터럴을 핸들러와 테스트 양쪽에 중복 기입하고 신규 생산 타입 참조를 배제한다(85행). 그 근거는 JSON **키** 검사에는 타당하지만(원시 키 검사라야 이름 오류를 잡는다) 상태 **값**에는 적용되지 않으므로, `HealthResponse`에 `public const string HealthyStatus = "Healthy";`를 두고 핸들러와 단언이 공유하면 기존 스타일과 더 맞는다. 필수는 아니다.
- **[취향]** 4절의 "시스템 시계 역행 시 시간 범위 검증 실패" 한계 기술은 정직하고 적절하다. `TimeProvider` 추상화를 비범위로 둔 판단(97행)도 컨텍스트 34행과 일치한다.

---

## 검증 완료 항목 (계획의 가정 중 원본과 대조해 참으로 확인한 것)

| 계획의 주장 | 대조 결과 |
|---|---|
| `Program.cs:51` = `WeatherForecast` record가 파일 하단에 위치 | 정확. `public record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)` |
| `Program.cs:35`, `:37` = 날씨 엔드포인트 등록 끝·`app.Run()` | 정확. 35행 `.WithName("GetWeatherForecast");`, 37행 `app.Run();` → 삽입 위치 타당 |
| `Program.cs:15` = `app.UseHttpsRedirection()` | 위치는 정확(해석은 [P-C1] 참조) |
| `Tests.csproj:4`/`:12`/`:14`/`:23` = net10.0 / Mvc.Testing 10.0.12 / xunit 2.9.3 / Api ProjectReference | 4건 모두 정확. 신규 패키지 불필요하다는 결론 타당 |
| `WeatherForecastEndpointTests.cs:20`/`:22`/`:34` = `IClassFixture` 구조 / 팩토리 선언 주석 / `HttpClient` 주석 | 3건 모두 정확. 기존 스타일 승계 근거로 적절 |
| `AGENTS.md:81`/`:85`/`:86`/`:87` = XML 주석 의무 / Thread Safety / Memory Allocation / Blocking | 4건 모두 정확 |
| `AGENTS.md:112`/`:115` = 인라인 근거 주석 규칙 | `:112` 정확. `:115`는 "주석 내용" 항목이고 대상 타입 목록은 `:114` — 같은 규칙 블록 내 인접 항목이라 논지에 영향 없음 |
| 추가 확인 — 응답 record를 `Program.cs` 하단에 두는 것이 최상위 문 파일에서 유효한가 | 유효. 기존 `WeatherForecast`(51행)·`public partial class Program`(71행)이 이미 `app.Run()` 뒤에 선언되어 있다 |
| 추가 확인 — `dotnet test WebProject.sln`에 테스트 SDK 없는 `WebProject.Sample`이 섞이는 문제 | 비이슈. `WebProject.sln:10`에 포함되지만 CI(`ci.yml:29`)가 이미 솔루션 전체에 같은 게이트를 돌리고 있어 신규 리스크가 아니다 |
| 추가 확인 — `WithName("GetHealth")` 이름 충돌 | 없음. 기존 이름은 `GetWeatherForecast` 하나뿐(`Program.cs:35`) |

## [미해결 질문]

1. **`plan/` 설계 문서 작성 여부.** CLAUDE.md의 플랜 문서화 규칙은 "기능 설계가 완료되면 `plan/<기능명>_<MMDD>.md` 작성"을 요구하지만, 컨텍스트(3행)는 이 작업을 "하네스 검증용 소형 기능"으로 규정하고 run 디렉터리 자체가 git 추적 대상이다. 이 run 산출물이 `plan/` 규칙을 대신하는지, 별도로 `plan/health_endpoint_0913.md`를 만들지는 오케스트레이터 정책 판단이 필요하다(Codex 계획의 결함이 아니라 미결 정책이라 지적에서 분리했다).
2. **엔드포인트 이름 검증의 필수 여부.** 컨텍스트 9행의 필수 테스트 항목에는 `WithName` 검증이 없고 8행에만 요구사항으로 적혀 있다. [P-C2]의 메타데이터 테스트를 필수 산출물로 볼지, 비용이 크면 드롭 가능한 선택 항목으로 볼지 통합 계획에서 확정해야 한다.

---

## 요약

| 심각도 | 건수 | 항목 |
|---|---|---|
| High | 0 | — |
| Med | 2 | P-C1(HTTPS 리디렉션 전제·스타일 충돌), P-C2(메타데이터 테스트 파일 목록 누락·메커니즘 미정) |
| Low | 3 | P-C3(camelCase 근거 미기재), P-C4(`JsonDocument` 인라인 주석 누락), P-C5(콘텐츠 타입 단언 방식 미정) |
| 취향 | 4 | 상태 값 상수화(기존 관례 정렬), CI 구성 정렬, `+00:00` 단언 축소, 시계 역행 한계 기술(현행 유지 의견) |
| 미해결 질문 | 2 | `plan/` 문서 정책, 엔드포인트 이름 검증의 필수 여부 |
