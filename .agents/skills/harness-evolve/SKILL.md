---
name: harness-evolve
description: "TDD 사이클 후 초기 요구사항과 최종 코드 사이의 델타(명확화·암묵 요구·구현 궤적·거부된 설계·커버리지 갭)를 증거 기반으로 기록한다. tdd-orchestrator Phase 4 또는 /harness-evolve 수동 호출."
---

# Harness Evolve Skill

TDD 사이클의 진화 궤적을 포착해 초기 명세와 최종 코드 사이의 갭을 분석한다. 오케스트레이터(메인 세션)가 직접 실행한다 — 별도 에이전트·SendMessage 없음.

## 입력 읽기 (`run_dir`은 `_workspace/tdd/latest.txt` 또는 인수)
```
{run_dir}/00_manifest.json                 ← 시도 횟수·판정·채택 trx (Green 시도 수의 유일한 근거)
{run_dir}/00_requirements.md, 00_requirements_c*.md   ← 요구사항 버전들 (T=0 은 첫 파일)
{run_dir}/01_analyst/test_design.md, Tests/, results/red_attempt*.trx
{run_dir}/02_builder/build_notes.md (시도별 누적), Src/
{run_dir}/03_qa/refactor_guide.md (시도별 누적), Src/ (있으면), results/qa_attempt*.trx, *_regression.trx
```
**최종 테스트 결과**는 manifest `final_results`가 가리키는 trx(리팩토링이 있었으면 `_regression.trx`)다. 리팩토링 전 로그를 최종으로 쓰지 않는다.
**최종 소스**는 `03_qa/Src`(우선) + `02_builder/Src`(나머지) — csproj 파일 우선순위와 동일.

## 델타 5차원

### Δ1 요구사항 명확화
| 초기 요구사항 | 구체화된 요구사항 | 발견 단계 | 근거 파일 |

### Δ2 발견된 암묵적 요구사항
| 발견 | 단계 | 테스트 이름 | 처리 | 근거 |

### Δ3 구현 진화 궤적 (동작 보존 리팩토링만 Refactor 행에)
| 단계 | 구현 | 변화 이유 |
| Red | 스텁 | 컴파일용 |
| Green (Fake) | `return 5` | 단일 테스트 |
| Green (일반화) | `return a + b` | 삼각측량 |
| Refactor | `const int MaxRetry = 3` 추출 / 메서드 분리 | 동작 보존 |
`checked(a + b)` 같은 정책 변경은 Refactor가 아니라 "다음 사이클 후보"에 적는다.

### Δ4 거부된 설계
| 항목 | 이유 | 단계 | 근거(build_notes/refactor_guide) |

### Δ5 테스트 커버리지 갭
| 미커버 경로 | 위치 | 권장 조치 |

## 증거 규율
- Green 시도 횟수·QA 판정·회귀 결과는 **manifest와 trx**에서만 가져온다. 없으면 "확인 불가"
- 요구사항 버전이 여러 개면 사이클별로 Δ1·Δ2를 나눈다

## 리포트 (`{run_dir}/04_evolution/evolution_report.md`)
```markdown
# TDD 진화 리포트
생성: {datetime} | run_id | 기능(사이클 목록)
## 사이클 요약
- Red: N개 테스트 (Happy/Edge/Error), Red 증빙 trx
- Green: 시도 N회 (재작업 N회) — manifest 근거
- Refactor: 제안 N / 적용 N / 롤백 N — regression trx
## 핵심 진화 포인트 (상위 3)
## 초기 명세 충실도: 요구 N개 중 N개 구현 / 암묵 요구 N개 / open_questions 잔존 N개
## 최종 테스트 현황: total/passed/failed (final_results trx) / 회귀 없음|N개
## 다음 TDD 사이클 추천 | 기능/케이스 | 근거 | 우선순위 |
## 확인 불가 항목
```
저장 후 오케스트레이터(현재 세션)가 Phase 5 보고에 요약을 포함한다.
