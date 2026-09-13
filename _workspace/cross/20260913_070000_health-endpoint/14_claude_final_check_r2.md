# 14. Claude 최종 계획 재검토 (라운드 2)

- 대상: `13_final_plan_r2.md`
- 입력: `00_context.md`, `12_plan_adjudication_r2.md`(+ 참고 `12_plan_adjudication.md`)
- 기준 커밋: `524f5a730867ce59e226bd4f9332961b8b8df6f8`
- 참조한 프로젝트 원본: `WebProject.Api/Program.cs`, `WebProject.Api/WebProject.Api.csproj`, `WebProject.Api.Tests/WebProject.Api.Tests.csproj`, `WebProject.Api.Tests/WeatherForecastEndpointTests.cs`, `.gitignore`, `git status` 실측
- 결과: **High 0 · Med 0 · Low 3 · 취향 2**, 승계 미해결 2건(모두 비차단)

## 1. 조정(r2) 채택 항목 ↔ 통합 계획 대조

| ID | 조정 요구 | r2 계획 반영 위치 | 판정 |
|---|---|---|---|
| X-F1 | 원시 문자열이 `Z` 또는 `+00:00` 으로 끝나는지 검사 추가, `TryGetDateTimeOffset`+`Offset==0`+시각 창 유지, 특정 표기 고정 금지 | §1 "UTC 표기" 행(L17), §6 Fact 1 6~8단계(L100~102: 접미사 `Assert.True(raw.EndsWith("Z") or raw.EndsWith("+00:00"))` → `TryGetDateTimeOffset` → `Offset==TimeSpan.Zero` → `InRange`) | **반영 O** |
| X-F2 | 307 폴백을 `CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = https://localhost })` 로 교체, 기본 경로는 `CreateClient()` 유지, `AllowAutoRedirect=false` 는 진단용 | §5 L84(폴백 문장·`20_impl_notes.md` 기록·"진단 용도로만" 명시), 기본 경로 유지 문구 동일 행 | **반영 O** |
| X-F3 (a) 멤버별 `<remarks>` 3축 | 생성자·Fact 1·Fact 2 각각 `<summary>`+`<remarks>` | §4 표 L69(생성자), L70(Fact 1), L71(Fact 2) — 3축 항목별 내용 명시 | **반영 O** |
| X-F3 (b) "각 테스트는 await" 문구 제거·Fact 2 동기 확정 | 클래스 `<remarks>` Blocking 을 메서드별로 분리, Fact 2 `public void` | §4 L68("Fact 1 은 … `await` 로 비동기 대기, Fact 2 는 동기 즉시 반환"), §6 L104("**동기 `public void`**") — r1 의 일괄 await 문구 소멸 | **반영 O** (단 P-C10 참조) |
| X-F4 / P-C6 | "2개"로 교정, 빈 행 삭제, run 디렉터리 허용 범위, 신규 파일 본문 확인 | §2 제목 L20("제품·테스트 코드 정확히 2개"), 표 2행만 존재(빈 행 없음), L27 허용 산출물 문장, §7-7 L119(`git diff --no-index -- /dev/null …`) | **반영 O** (검사 명령 정밀도는 P-C11) |
| 비고: P-X3·P-X10·P-C1 부분 반영 해소 | X-F2/F3/F4 로 해소 | 위 3행 | **반영 O** |
| 비고: §9 "HTTPS 포트가 결정되는 경우" 조건 | 문구 교정 | §9 L128("**HTTPS 포트가 결정되는 환경에서는** 평문 접근 시 307") | **반영 O** |
| P-C7 | `using var response` 근거 기록, `LinkGenerator` 선언 규칙 대상 여부 명기 | §4 L72(`HttpResponseMessage` 소유권 근거 + 정본 파일 대비 차이 명시), L73(`LinkGenerator` **비대상** 근거) | **반영 O** |
| P-C8 | using 5종 명시, `GetPathByName` 오버로드 노트 | §6 L92(5개 using), L106(CS0121 시 `(object?)null`) | **반영 O** (취향 2 참조) |
| [취향 기각] 생성자·Fact `<summary>` 스타일 갈림 | 기각(방향 반대), 후속 과제로 승계 | §9 L129(후속 과제 승계) | **반영 O** |

→ 조정 r2 채택 항목 **9/9 전부 반영**. 미반영·왜곡 없음.

## 2. 라운드 2 편집이 만든 모순·기술 오류 검증

### (a) 접미사 검사와 STJ 직렬화 출력의 정합 — **정합함**
- `DateTimeOffset` 의 STJ 기본 직렬화는 ISO 8601-1:2019 확장 형식이며 오프셋 0 은 `…+00:00` 으로 쓴다(`Z` 로 축약하지 않음). 따라서 `EndsWith("+00:00")` 분기가 실제 출력과 일치하고 단정은 통과한다.
- X-F1 의 전제도 정확하다. `JsonElement.TryGetDateTimeOffset` 는 오프셋 토큰(`Z`/`+`/`-`)이 없으면 **로컬 시간으로 해석**하는 경로를 타므로, UTC 환경(CI `windows-latest` 포함)에서는 오프셋 없는 문자열도 `Offset == TimeSpan.Zero` 를 통과한다. 접미사 검사는 이 구멍을 실제로 막는다.
- `Assert.InRange(generatedAt, startedAt, finishedAt)` 는 `DateTimeOffset` 의 `IComparable` 구현(UTC 기준 비교)을 쓰므로 유효하다.

### (b) 307 폴백(`BaseAddress = https://localhost`) — **타당함**
- `WebApplicationFactory<T>.CreateClient(WebApplicationFactoryClientOptions)` 는 공개 API 이고 `BaseAddress` 는 설정 가능하다. TestServer 의 `ClientHandler` 는 요청 URI 의 스킴으로 `HttpRequest.Scheme` 을 구성하므로 `https` 기준 주소면 `IsHttps == true` 가 되어 `UseHttpsRedirection()` 이 리다이렉트하지 않고 통과한다. 결과적으로 200·본문 검증이 가능하다(실제 TLS 핸드셰이크는 없음 — 인메모리 파이프라인 그대로).
- 반면 `AllowAutoRedirect=false` 는 307 응답 자체를 돌려줄 뿐이라 200 계약을 검증하지 못한다는 X-F2 지적이 옳고, 계획이 이를 "진단 용도"로 한정한 것도 맞다.

### (c) Fact 2 동기 `void` 에서 `_factory.Services` 접근 — **문제 없음(문구만 부정확)**
- `WebApplicationFactory<T>.Services` 는 `EnsureServer()` 를 호출해 호스트를 **동기적으로** 기동한 뒤 서비스 프로바이더를 반환한다. xUnit 동기 테스트는 `SynchronizationContext` 없는 스레드 풀 컨텍스트에서 실행되므로 sync-over-async 데드락 위험이 없다. 기능상 안전하다.
- 다만 "즉시 반환"이라는 Blocking 기술은 사실과 어긋난다 → P-C10.
- `LinkGenerator` 조회 경로 자체도 유효하다: `WithName("GetHealth")` 가 `IEndpointNameMetadata` 를 붙이고 `EndpointNameAddressScheme` 이 이를 조회하므로 `GetPathByName("GetHealth", values: null)` → `"/health"`.

### (d) 멤버별 `<remarks>` 계획과 프로젝트 규칙·기존 코드의 부합 — **부합함**
- CLAUDE.md 는 "public 클래스의 메서드"에 3축(`Thread Safety`/`Memory Allocation`/`Blocking`) `<remarks>` 를 요구한다. 신규 테스트 클래스는 `public sealed`, 멤버도 public 이므로 규칙이 문자 그대로 적용된다 → X-F3 채택이 규칙에 맞다.
- 기존 정본 `WeatherForecastEndpointTests.cs` 는 클래스에만 `<remarks>`(L7~19)를 달고 생성자(L26)·Fact(L31~32)는 무주석이다. 계획이 "기존 파일 무변경 + 신규 파일은 상위 수준 문서화"로 정리한 것은 컨텍스트의 기존 무변경 원칙과 충돌하지 않는다.
- P-C7 의 근거 인용도 실측과 일치한다: `WeatherForecastEndpointTests.cs:37` 은 `var response = await client.GetAsync("/weatherforecast");` 로 `using` 이 없다.
- `LinkGenerator` 를 선언부 인라인 주석 **비대상**으로 판정한 것도 타당하다. CLAUDE.md 대상 목록은 `Socket`/`Pipe`/`Channel<T>`/`ArrayPool<T>`/`Memory<T>`/`SemaphoreSlim` 등 네트워크·메모리 프리미티브이며 라우팅 조회 서비스는 여기에 없다.
- §6 의 using 5종도 정확하다. `WebProject.Api.Tests.csproj` 는 `Microsoft.NET.Sdk`(웹 SDK 아님)이므로 ImplicitUsings 가 `Microsoft.AspNetCore.*`·`Microsoft.Extensions.DependencyInjection` 을 넣어주지 않고, `<Using Include="Xunit" />` 하나만 추가돼 있다.
- §7-4 의 "CS1591 없음" 근거도 확인: 두 csproj 모두 `GenerateDocumentationFile` 미설정이다.
- §2.1 의 람다 화살표와 식 사이 `//` 주석은 트리비아라 컴파일에 문제없다.

## 3. 지적 (P-C9~)

**[P-C9] Low | `WebProject.Api.Tests.csproj:6` 이 `<Nullable>enable</Nullable>` 이므로 §6 Fact 1 6단계의 `gen.GetString()` 은 `string?` 이고, 그대로 `raw.EndsWith("Z")` 를 호출하면 CS8602(null 가능 참조 역참조) 경고가 난다. 이는 §7-4 의 "신규 경고 0" 게이트와 직접 충돌해 구현 중 1사이클을 낭비시킨다. | 제안: 접미사 검사 앞에 `var raw = gen.GetString(); Assert.NotNull(raw);` 를 넣어 xUnit 의 `[NotNull]` 사후 조건으로 nullable 을 좁힌다. 이 관용구는 이 저장소에서 이미 경고 없이 쓰이고 있다(`WeatherForecastEndpointTests.cs:42-43` — `Assert.NotNull(forecasts);` 직후 `forecasts.Length` 접근, 동일 csproj·xunit 2.9.3). `!` 억제 연산자보다 기존 스타일에 맞다.**

**[P-C10] Low | Blocking 기술이 자기모순이다. §4 L71 은 Fact 2 를 "Blocking: 즉시 반환(첫 호출 시 fixture 호스트 기동은 `_factory.Services` 가 동기 수행)" 이라 적어 한 문장 안에서 "즉시 반환"과 "동기 기동"이 충돌한다. 같은 결함이 한 행 위 L68 클래스 `<remarks>` 에도 있다 — "Fact 1 은 … `await` 로 비동기 대기(동기 블로킹 없음)" 이지만 `_factory.CreateClient()` 역시 내부적으로 `EnsureServer()` 로 호스트를 동기 기동하므로, 먼저 실행되는 Fact 가 그 비용을 문자 그대로 동기 블로킹으로 지불한다. | 제안: 두 행을 한 문장으로 통일한다 — "fixture 호스트 기동은 최초 접근 시 1회 동기 블로킹(`CreateClient()`/`Services` 공통), 이후 호출은 즉시 반환. Fact 1 의 HTTP 요청 자체는 `await` 비동기 대기, Fact 2 는 조회만으로 즉시 반환."**

**[P-C11] Low | §7-7 의 종료 검사 명령 `git status --short` 는 미추적 디렉터리를 접어 출력하므로 "그 외는 `_workspace/cross/<run>/**` 산출물뿐"을 실제로 검증하지 못한다. 실측: 현재 저장소에서 `git status --short` 의 출력은 `?? _workspace/` 한 줄뿐이고, `git status --short --untracked-files=all` 을 줘야 `?? _workspace/cross/20260913_070000_health-endpoint/00_context.md` … 처럼 run 하위 파일이 전개된다. 접힌 상태에서는 다른 하네스 디렉터리에 파일이 새로 생겨도 동일한 한 줄로 보여 차이를 놓친다. | 제안: 검사를 두 번으로 나눈다 — (1) 제품 코드 단정은 경로 한정으로 `git status --short --untracked-files=all -- WebProject.Api WebProject.Api.Tests` (기대: `M WebProject.Api/Program.cs`, `?? WebProject.Api.Tests/HealthEndpointTests.cs` 2줄만), (2) 허용 범위 단정은 무한정 `git status --short --untracked-files=all` 1회로 run 디렉터리 외 신규 경로가 없음을 확인. §7-1 의 시작 기준선도 같은 옵션으로 떠야 비교가 성립한다.**

### 취향 (심각도 없음)

- **[취향]** `raw.EndsWith("Z")` 는 기본 문화권 비교 오버로드다. ASCII 리터럴이라 결과는 동일하고 기본 활성 분석기도 없어 경고가 나지 않지만, 문자열 계약 검사는 `StringComparison.Ordinal` 을 명시하는 편이 의도가 분명하다. (조정이 확정한 "둘 다 허용" 정책 자체는 지지한다 — `DateTimeOffset` 경로에서 `Z` 분기는 현재 도달하지 않지만, 향후 `DateTime.UtcNow` 로 바뀌면 `Z` 가 나오므로 방어값으로 두는 편이 안전하다.)
- **[취향]** §6 L106 의 CS0121 노트는 불필요하다. `GetPathByName` 의 다른 오버로드는 첫 인자가 `HttpContext` 라 `string` 리터럴이 바인딩될 수 없어 실제 모호성이 생기지 않는다. 해가 없으니 남겨도 무방하고, 지우면 반 줄 절약이다.

## 4. 남은 미해결 항목의 차단성 판정

§9 의 승계 미해결 2건은 **모두 비차단**이다.
1. Production 에서 `/health` 가 `UseHttpsRedirection()` 뒤라 HTTPS 포트가 결정되는 환경에서 평문 접근 시 307 — 컨텍스트 요구사항(200·JSON 계약·엔드포인트 이름·통합 테스트) 밖의 배포 정책 사안이고, 파이프라인 순서를 건드리면 "기존 코드 최소 변경" 제약을 깬다. `90_final_report.md` 승계가 맞다.
2. 기존 테스트 파일과 신규 파일의 문서화 수준 차이 — "기존 테스트는 변경하지 않는다"가 컨텍스트 명시 제약이므로 지금 정비하면 오히려 요구사항 위반이다. 후속 과제 승계가 맞다.

P-C9~P-C11 도 차단성이 없다. P-C9 는 계획 자신의 §7-4 경고 게이트가 잡아내는 자가 교정 항목(구현 1사이클 비용이라 기록해 둘 가치는 있음), P-C10 은 주석 문구, P-C11 은 검증 명령 정밀도 문제다. 계획 골격(설계 결정·변경 파일 2개·테스트 2건·주석 규칙 적용·검증 순서)은 요구사항과 프로젝트 규칙을 모두 충족하며, 조정 r2 의 채택 항목이 빠짐없이 반영됐다.

VERDICT: APPROVE
