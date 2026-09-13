# 32. 수정 노트 (리뷰 수정 라운드 1) — `GET /health`

- run: `20260913_070000_health-endpoint`
- base_sha: `524f5a730867ce59e226bd4f9332961b8b8df6f8`
- 입력: `31_review_adjudication.md` (유효 판정 2건: R-C1, R-C2). 참고: `13_final_plan_r2.md`, `20_impl_notes.md`, `20_test_results.txt`
- 시도 횟수: **1** (빌드·테스트 1회 통과, 재시도 없음)
- 커밋: **하지 않음.** `.git/` 아래 어떤 파일도 생성·수정·삭제하지 않았다(`auto_commit_msg.txt`·`harness_commit_in_progress` 포함).
  `git stash`·`git checkout` 등 워크트리를 변경하는 명령도 사용하지 않았다.
- 검증 원문: `32_test_results.txt` + 캡처 파일 5종(`32_baseline_build_capture.txt`, `32_build_capture.txt`, `32_build_capture_noincremental.txt`, `32_test_capture_filtered.txt`, `32_test_capture_all.txt`)

## 1. 지적별 수정 내역

### R-C1 (Low, 유효) — 빌드 게이트 검증 출력이 수기 전사라 원문 미보존

**수정: 적용.** 이번 라운드의 모든 검증 출력을 `>` 리다이렉트로 캡처해 run 디렉터리에 원문 파일로 보존하고, `32_test_results.txt` 에 전문 인용 + 요약을 넣었다. 코드 변경은 없다.

| 게이트 | 명령(실제 실행) | 캡처 파일 | 결과 |
|---|---|---|---|
| 베이스라인 빌드 | `git archive 524f5a73… \| tar -xf - -C <스크래치>/baseline` 후 그 트리에서 `dotnet build WebProject.sln -c Release` | `32_baseline_build_capture.txt` | 0 Warning / 0 Error, exit=0 |
| 구현 후 빌드 | `dotnet build WebProject.sln -c Release` | `32_build_capture.txt` | 0 Warning / 0 Error, exit=0 |
| 구현 후 빌드(증분 배제) | `dotnet build WebProject.sln -c Release --no-incremental` | `32_build_capture_noincremental.txt` | 0 Warning / 0 Error, exit=0 |
| 필터 테스트 | `dotnet test WebProject.sln -c Release --no-build --filter "FullyQualifiedName~HealthEndpointTests"` | `32_test_capture_filtered.txt` | Total 2 / Passed 2 / Failed 0 / Skipped 0, exit=0 |
| 전체 테스트 | `dotnet test WebProject.sln -c Release --no-build` | `32_test_capture_all.txt` | Total 11 / Passed 11 / Failed 0 / Skipped 0, exit=0 |

위 표의 명령은 모두 `DOTNET_CLI_UI_LANGUAGE=en` 환경변수를 앞에 두고 실행했다(캡처 원문이 영어인 이유). 재현 시 동일 표기를 얻으려면 같은 접두를 붙일 것.

세부 조치:
1. **베이스라인은 워크트리 무변경으로 재현.** 스크래치 디렉터리(`C:\Users\aaa\AppData\Local\Temp\claude\E--project-WebProject\4f775bcd-7956-42df-9a92-6924fb67f91c\scratchpad\baseline`, 프로젝트 밖)에 기준 커밋 트리를 `git archive`로 풀고 그 트리에서 빌드했다. 조정 기록이 "재현 가능"이라 판단한 절차를 그대로 실행해 경고 0 을 원문으로 확보했다.
2. **종료 코드 보존.** 각 캡처 파일 마지막 줄에 셸이 `exit=<code>` 를 덧붙이도록 해 통과 판정이 출력 문자열 파싱에만 의존하지 않게 했다.
3. **로케일 고정.** `DOTNET_CLI_UI_LANGUAGE=en` 으로 5개 캡처를 통일했다. 한국어 콘솔 출력이 리다이렉트 시 깨질 위험을 제거하고, 베이스라인·구현 후 캡처를 같은 표기로 직접 대조할 수 있게 하기 위함이다(라운드 1 은 한국어 출력, 수치·게이트 의미 동일).
4. **증분 빌드로 경고가 숨는 문제 차단.** 이번 라운드는 테스트 프로젝트만 변경돼 증분 빌드가 다른 프로젝트를 재컴파일하지 않을 수 있으므로, 베이스라인(클린 트리 전체 컴파일)과 동일 조건 대조를 위해 `--no-incremental` 재빌드 캡처를 추가했다. 세 캡처 모두 0 Warning.
5. **실행 순서 보장.** 소스 수정 → 빌드 → 테스트 순으로 실행했다. 두 테스트 명령이 `--no-build` 라 빌드를 선행하지 않으면 수정 전 DLL 로 통과할 수 있어, 이 순서 자체가 새 단정 실행의 근거다(검증 한계는 `32_test_results.txt` [5] 절에 명시).
6. 라운드 1 산출물(`20_test_results.txt`, `20_impl_notes.md`)은 **수정하지 않았다.** R-C1 의 해소는 이번 라운드의 새 증빙으로 하며, 과거 기록을 소급 재작성하지 않는다.

### R-C2 (Low, 유효) — 응답 JSON 속성 집합 미단정(제3 필드 추가 회귀 미검출)

**수정: 적용.** `WebProject.Api.Tests/HealthEndpointTests.cs` Fact 1(`GetHealth_Returns200WithHealthyStatusAndUtcTimestamp`) 에 단정 1줄 + `//` 주석 1줄을 추가했다(앞 빈 줄 1 포함 총 3줄 삽입, 126줄 → 129줄).

- 위치: `generatedAt` 의 `TryGetProperty`·`ValueKind` 검사 직후, `var raw = gen.GetString();` 앞 (파일 88~89행).
  - 지시가 허용한 두 위치(루트 Object 단정 직후 / status·generatedAt 검사 직후) 중 **후자**를 택했다. 필드명이 바뀌는 회귀에서는 앞선 `TryGetProperty` 단정이 "응답에 status 속성이 없습니다" 같은 구체적 메시지로 먼저 실패하고, 개수 단정은 "필드가 추가됐다"는 별개 회귀에만 반응해 진단이 명확해진다.
- 추가된 코드:
  - `// 요구사항의 응답 계약은 status·generatedAt 2개 필드뿐이므로, 제3 필드가 추가되는 회귀를 속성 개수로 잡는다.`
  - `Assert.Equal(2, doc.RootElement.EnumerateObject().Count());`
- `System.Linq`: 테스트 csproj 의 `<ImplicitUsings>enable</ImplicitUsings>` 로 이미 스코프에 있어 `using` 추가가 불필요했다(`JsonElement.ObjectEnumerator` 에는 `Count` 속성이 없고 `Enumerable.Count<JsonProperty>` 확장 메서드가 필요하다). 빌드 0 Warning / 0 Error 로 실측 확인.
- 그 외 코드 변경 없음: `WebProject.Api/Program.cs` 는 이번 라운드에 편집하지 않았다. 근거는 두 가지다.
  (a) 이 세션에서 `Program.cs` 에 어떤 편집 명령·도구도 사용하지 않았다.
  (b) `git apply --check -R --include=WebProject.Api/Program.cs 30_diff.patch` 가 성공한다 → 리뷰 대상 diff 를 현재 워크트리에 역적용할 수 있으므로 **내용이 리뷰 시점과 동일**하다(줄 수 일치(`numstat 20 0`)보다 강한 근거).

## 2. 미적용 항목 (조정 기록 A절)

| 항목 | 판정 | 이번 라운드 조치 |
|---|---|---|
| [취향-1] `HealthResponse` → `sealed record` | 취향 (미적용) | **해당 없음** — 수정하지 않음. 기존 `WeatherForecast` 와의 일관성 우선이라는 조정 결론 유지 |
| [취향-2] 클래스 `<remarks>` "병렬 테스트 실행에 안전" → "클래스 간 병렬" 한정 | 취향 (미적용) | **해당 없음** — 수정하지 않음. 오류가 아니라는 조정 결론 유지, 문구 일괄 정비는 후속 과제 |
| Codex "결함 없음" 근거 8축 | 유효, 수정 방향 "없음" | **해당 없음** — 코드·문서 수정 대상 아님 |
| Codex 표현 제한 2건(`--no-build` ≠ 강제 재컴파일, 삭제 0줄 ≠ 동작 보존 증명) | 기록 | 코드 수정 없음. 최종 보고서 이월 사항이나, 이번 라운드 검증의 성격을 오해하지 않도록 `32_test_results.txt` [5] "검증 한계" 절에 동일 취지로 기재 |
| D절 승계 3건(운영 HTTPS 307 / 시스템 시계 / 기존 테스트 문서화 수준 차이) | 비차단 승계 | **해당 없음** — 이번 라운드 수정 대상 아님, `90_final_report` 로 이월 |
| 기각 항목 | 0건 | 없음 |

## 3. 변경 파일

| # | 파일 | 종류 | 이번 라운드 변경 |
|---|---|---|---|
| 1 | `WebProject.Api.Tests/HealthEndpointTests.cs` | 수정(미추적 신규 파일) | Fact 1 에 속성 개수 단정 1줄 + `//` 주석 1줄(+빈 줄 1) |
| 2 | `WebProject.Api/Program.cs` | **무변경** | 없음 (라운드 1 상태 그대로) |

run 산출물: `32_fix_notes.md`, `32_test_results.txt`, 캡처 5종. 그 외 프로젝트 파일·csproj·sln·CI·`plan/`·다른 run 디렉터리·하네스 파일은 건드리지 않았다.

**보고 JSON 기준:** `changed_files` 는 **이번 라운드에 실제로 편집한 소스 파일 수 = 1**(`HealthEndpointTests.cs`) 이다. 라운드 1 노트의 `2` 는 base 대비 누적 변경 파일 수 기준이었으므로 기준이 다르다(`Program.cs` 는 이번 라운드 무변경). `tests` 는 전체 테스트 실행(11/0/0) 수치이며 필터 실행(2/2)은 결과 파일에 별도 보존했다.

## 4. 변경 범위 최종 확인

```
$ git status --short --untracked-files=all -- . ':(exclude)_workspace'
 M WebProject.Api/Program.cs
?? WebProject.Api.Tests/HealthEndpointTests.cs
```

정확히 2줄임을 종료 시점에 확인했다. 이번 라운드에 추가한 캡처 파일 5종·노트·결과 파일은 모두 `_workspace/cross/20260913_070000_health-endpoint/` 안이라 위 pathspec 에서 제외되며, 베이스라인 빌드는 프로젝트 밖 스크래치 디렉터리에서 수행해 저장소에 산출물을 남기지 않았다.

## 5. [범위 이탈]

**없음.** 지시가 허용한 테스트 1줄(+주석) 외 소스 변경이 없고, 신규 공개 API·파일 추가·패키지 추가·설계 변경이 없다. 기각·취향 항목은 건드리지 않았다.

## 6. 관측 (조치 없음)

`.git/harness_commit_in_progress` 센티널이 디스크에 존재한다(mtime `2026-09-13 15:12`). 이 수정 세션의 첫 검증 명령(베이스라인 빌드 캡처 mtime `16:15:28`)보다 이르므로 오케스트레이터가 만든 것이며, 구현자는 생성·수정·삭제하지 않았다(라운드 1 노트와 동일 관측).
