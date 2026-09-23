# doc-harness — 문서화 하네스

프로젝트 전체를 **다단계 Claude 파이프라인**으로 분석해 `docs/generated/`에 신규 개발자용 기술 문서를 만들고, 이후에는 `문서화` 한마디로 **변경분만 증분 갱신**한다. 코드가 Source of Truth이고, 모든 다이어그램은 Mermaid로 문서 안에 원본 그대로 존재한다.

설계: `docs/superpowers/specs/2026-09-23-doc-harness-design.md` · 구현 계획: `docs/superpowers/plans/2026-09-23-doc-harness.md`

## 사용

Claude Code 세션에서 `문서화`라고만 입력하면 된다(`.claude/skills/doc-harness`). 직접 실행하려면:

```bash
cd doc-harness && npm ci
npm run harness -- run            # 자동 판정: 최초면 INITIAL, 이후 INCREMENTAL, 변경 없으면 종료
npm run harness -- run --full     # baseline 무관 전체 재분석
npm run harness -- status         # 동기화 상태(LLM 호출 없음)
npm run harness -- verify         # 문서 수정 없이 검증만 (--no-llm: 결정적 검사만)
npm run harness -- resume         # 미완료 Run 이어가기
npm run harness -- run --phase discovery   # 개발용: 이 단계까지만(커밋 안 함)
npm run harness -- run --feature F003      # 개발용: 기능 하나만
npm run harness -- report [run-0003]
npm run harness -- clean [--runs|--all]
npm test && npm run typecheck
```

실제 `claude` CLI를 부르는 통합 테스트: `DOC_HARNESS_LIVE=1 npm test`.

## 구조

| 경로 | 역할 |
|---|---|
| `config/harness.yaml` | 제외 규칙, 재시도·검증 횟수, 모델·예산(phase별), 출력 경로 |
| `prompts/` | `00_global_rules.md`(모든 호출에 앞부분으로 붙음) + 단계별 프롬프트 |
| `schemas/` | 산출물 JSON 스키마. `common.schema.json`의 `$defs`를 인라인해 `--json-schema`로 넘긴다 |
| `src/claude.ts` | `claude -p --tools Read,Glob,Grep --setting-sources user …` 어댑터. 에이전트는 파일을 쓰지 못한다 |
| `src/run.ts` | Run 트랜잭션: `staging/`에 만들고 검증 통과 후에만 정본 교체 |
| `src/change/` | 해시+git 변경 감지 → LLM 분류 → 결정적 영향 분석 → 기능 델타 |
| `src/phases/` | Inventory · Architecture · Discovery · Feature(워커 풀) · Data/API · Failures · Operations |
| `src/render/` | 섹션 앵커(`<!-- doc-harness:section … -->`) 단위 렌더/교체, 템플릿 문서, 서술 문서, 다이어그램 조정 |
| `src/verify/` | 결정적 검사(참조·Mermaid 3층·교차 일관성) + LLM 검증·일관성 + 수정 루프 |
| `workspace/` | `baseline.json` · `depgraph.json` · `current/`(정본 산출물) · `runs/run-NNNN/`(state·changes·impact·report·staging·logs) · `inbox/session_context.md` |

## 원칙

- 근거 우선순위: 실제 코드 > Git > 테스트 > 기존 문서 > 세션 맥락 > 추론. 모든 주장은 `CONFIRMED|INFERRED|UNKNOWN|POSSIBLE_LEGACY|POTENTIAL_ISSUE` + evidence.
- 프로덕션 코드는 절대 수정하지 않는다(에이전트에 쓰기 도구가 없다). 하네스가 쓰는 곳은 `doc-harness/workspace`와 `docs/generated`뿐이다.
- Baseline은 Verification 성공 후에만 갱신된다. 실패한 Run은 `runs/run-NNNN/`만 남긴다.
- 사람이 문서의 관리 섹션을 고치면(앵커 해시 불일치) 증분 실행이 보존한다. 코드와 충돌이 검증으로 확인될 때만 교체하고 `run.json.manualEditsOverridden`에 남긴다.
