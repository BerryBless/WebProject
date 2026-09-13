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
