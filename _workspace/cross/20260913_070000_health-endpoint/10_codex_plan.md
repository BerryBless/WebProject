**`Program.cs`에 엔드포인트와 응답 record를 추가하고, 별도의 통합 테스트 파일에서 HTTP·JSON·UTC 시각 계약을 검증한다.** 프로젝트 소스와 `AGENTS.md`를 근거로 수립한 독립 계획이며, `_workspace/cross/`는 읽지 않았다. 파일 변경과 테스트 실행은 하지 않았다.

**1. 변경 파일 목록**

| 파일 | 변경 요지 |
|---|---|
| `WebProject.Api/Program.cs` | `app.Run()` 앞에 `GET /health`와 `.WithName("GetHealth")` 추가. 하단에 XML 문서 주석을 갖춘 `HealthResponse` record 추가 |
| `WebProject.Api.Tests/HealthEndpointTests.cs` | 신규 통합 테스트 클래스 추가. 응답 코드, JSON 계약, UTC 오프셋과 시각 범위 검증 |

응답 record는 **`Program.cs`에 둔다.** 기존 `WeatherForecast`가 같은 파일 하단에 있는 구조를 따르고, 두 속성뿐인 모델을 위해 파일을 분리할 필요가 작다. 근거: `WebProject.Api/Program.cs:51`.

프로젝트·패키지 설정 변경은 필요 없다. 테스트 프로젝트에 net10.0, MVC Testing 10.0.12, xUnit 2.9.3 및 API 프로젝트 참조가 이미 있다. 근거: `WebProject.Api.Tests/WebProject.Api.Tests.csproj:4`, `:12`, `:14`, `:23`.

**2. 공개 API 영향**

- HTTP API 추가: `GET /health`, 엔드포인트 이름 `GetHealth`.
- 정상 응답: HTTP 200, `application/json`, `status`와 `generatedAt` 속성을 가진 객체.
- C# 공개 타입 추가: `HealthResponse(string Status, DateTimeOffset GeneratedAt)`.
- 기존 공개 API의 시그니처나 동작 변경은 없다.

핵심 구현은 다음 형태로 한다. record의 상세 XML 주석은 구현 시 함께 작성한다.

```csharp
app.MapGet("/health", static () =>
    new HealthResponse("Healthy", DateTimeOffset.UtcNow))
    .WithName("GetHealth");
```

`DateTimeOffset.UtcNow`는 **요청 처리 때마다 한 번** 평가한다. 시작 시각이나 캐시된 응답을 재사용하지 않는다. 날짜를 문자열로 직접 포맷하지 않고 기존 JSON 직렬화 경로에 맡긴다.

삽입 위치는 기존 날씨 엔드포인트 등록 뒤, `app.Run()` 앞이다. 근거: `WebProject.Api/Program.cs:35`, `:37`.

**3. 동시성·메모리 정책과 주석**

- **Thread Safety:** 캡처 없는 `static` 핸들러가 요청별 record를 생성한다. 공유 가변 상태가 없어 락이 필요 없다.
- **Memory Allocation:** 요청마다 응답 record 객체 1개를 할당한다. `"Healthy"`는 상수 문자열을 재사용하고, 시각은 값 타입으로 보관한다. JSON 직렬화와 HTTP 응답 처리까지 무할당이라고 주장하지 않는다.
- **Blocking:** 핸들러는 시각 조회와 객체 생성 후 즉시 반환한다. 동기 I/O나 대기 작업이 없어 `async`, `Task.Run`, `ValueTask`를 도입하지 않는다.
- **소유권:** 응답 객체는 요청 처리에 사용되며 별도로 저장하지 않는다. 풀 버퍼나 수동 반환 자원은 없다.

새 record의 타입·생성자 매개변수·공개 속성을 문서화하고, 새 테스트 클래스·생성자·테스트 메서드에도 XML 주석을 작성한다. `<remarks>`에는 Thread Safety, Memory Allocation, Blocking을 명시한다. 근거: `AGENTS.md:81`, `:85`, `:86`, `:87`.

테스트의 `WebApplicationFactory<Program>`와 `HttpClient` 선언에는 인메모리 `TestServer` 및 메시지 핸들러를 통해 소켓 통신 없이 요청 파이프라인을 실행한다는 선택 근거를 인라인 주석으로 작성한다. 근거: `AGENTS.md:112`, `:115`, `WebProject.Api.Tests/WeatherForecastEndpointTests.cs:22`, `:34`.

팩토리는 xUnit fixture가 소유하며, 테스트에서 생성한 클라이언트·응답·`JsonDocument`는 `using`으로 해제한다.

**4. 예외·실패 경로 처리**

입력이나 외부 의존성이 없어 별도의 검증 실패·비정상 건강 상태 분기는 추가하지 않는다. 직렬화 오류나 연결 중단을 잡아서 정상 응답으로 바꾸지 않고 기존 ASP.NET Core 처리 경로에 맡긴다.

기존 HTTPS 리디렉션 설정은 유지한다. 신규 테스트는 HTTPS 기준 주소와 자동 리디렉션 비활성화를 사용해 최종 응답만 보고 중간 리디렉션을 놓치지 않도록 한다. 근거: `WebProject.Api/Program.cs:15`.

JSON 필드 누락, 잘못된 자료형, 날짜 파싱 실패는 테스트 실패로 처리한다. 시각 검증에는 임의 허용 오차나 재시도를 넣지 않는다. 다만 실행 중 시스템 시계가 역행하면 시간 범위 검증이 실패할 수 있다는 한계는 있다.

**5. 테스트 전략**

`HealthEndpointTests`는 기존과 동일한 `IClassFixture<WebApplicationFactory<Program>>` 구조를 사용한다. 근거: `WebProject.Api.Tests/WeatherForecastEndpointTests.cs:20`.

필수 통합 테스트 1개는 다음 순서로 검증한다.

1. 테스트 메서드 시작에 `startedAt = DateTimeOffset.UtcNow` 기록.
2. 클라이언트를 생성하고 `GET /health` 호출.
3. HTTP 200과 JSON 콘텐츠 유형 확인.
4. 응답을 `JsonDocument`로 읽고 객체 형태 및 정확한 `status`, `generatedAt` 필드 확인.
5. `status`가 문자열 `"Healthy"`인지 확인.
6. `generatedAt`이 문자열이고 ISO 8601 날짜로 `DateTimeOffset` 파싱되는지 확인. 문자열에 `Z` 또는 `+00:00`이 명시되었는지도 확인.
7. 파싱한 값의 `Offset == TimeSpan.Zero` 확인.
8. `finishedAt = DateTimeOffset.UtcNow`를 기록하고 `Assert.InRange(generatedAt, startedAt, finishedAt)` 검증.

원본 JSON을 검사하므로 응답 모델을 그대로 역직렬화하면서 필드 이름 오류를 놓치는 일을 피할 수 있다.

추가로 엔드포인트 메타데이터 테스트에서 `/health`의 `IEndpointNameMetadata.EndpointName == "GetHealth"`를 검증한다. 이는 HTTP 응답 본문만으로 확인할 수 없는 요구사항이다.

실행 명령:

```powershell
dotnet test WebProject.Api.Tests/WebProject.Api.Tests.csproj --filter FullyQualifiedName~HealthEndpointTests
dotnet test WebProject.sln
```

기존 `WeatherForecastEndpointTests`, `WeatherForecastTests`, `WeatherForecastRangeTests`는 수정 없이 전체 솔루션 테스트로 회귀 검증한다.

**6. 단계별 구현 순서**

- [ ] 기존 솔루션 테스트를 실행해 기준 상태를 확인한다.
- [ ] 신규 통합 테스트와 XML·인라인 주석을 작성한다. 생산 코드의 새 타입에 의존하지 않도록 JSON을 직접 검사한다.
- [ ] 신규 테스트를 실행해 미등록 `/health` 때문에 실패하는지 확인한다.
- [ ] `Program.cs`에 문서화된 `HealthResponse` record와 엔드포인트 등록을 추가한다.
- [ ] 신규 HTTP 계약 테스트와 엔드포인트 이름 테스트를 통과시킨다.
- [ ] `dotnet test WebProject.sln`으로 기존 동작을 검증한다.
- [ ] 변경 범위가 위 두 파일이며 기존 날씨 코드·테스트·패키지 설정이 그대로인지 확인한다.

**7. 비범위**

- HealthChecks 프레임워크, DB·외부 서비스 점검
- 인증, 레이트 리밋, OpenAPI 스키마 커스터마이징
- `/weatherforecast` 및 기존 테스트 변경
- 시간 공급자 추상화, 응답 캐싱, 메모리 풀링
- NuGet 패키지 추가, 하네스 변경, 커밋·푸시

이번 요청은 파일 변경을 금지하므로 `AGENTS.md`의 일반적인 계획 문서 저장 규칙과 별개로 계획은 이 응답으로만 제공한다.