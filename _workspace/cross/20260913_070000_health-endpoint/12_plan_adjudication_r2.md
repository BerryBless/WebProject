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
