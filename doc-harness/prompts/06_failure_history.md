# Phase 06 — Failure History(왜 지금 모습이 되었는가)

이 단계는 일반 코드 분석과 분리된다. 목표: **현재 코드가 왜 이런 모습인지**, 과거에 무엇이 실패했고 어떤 workaround가 남아 있는지 조사한다.

## 근거

1. 아래 Git 이력(키워드 히트 커밋의 메시지·변경 파일·diff 발췌).
2. 코드 마커(TODO/FIXME/HACK/workaround/fallback/retry/deprecated/legacy).
3. 저장소의 설계·보고 문서 경로(최하위 근거 — 내용은 코드·커밋으로 확인한 만큼만 사용).
4. 세션 맥락(있다면, 최하위).

## 규칙

- **커밋 메시지만 보고 원인을 사실처럼 단정하지 않는다.** 코드·diff로 확인되면 `CONFIRMED`, 메시지·문서에서만 보이면 `INFERRED`, 원인이 안 보이면 `UNKNOWN`.
- 실패 기록을 찾지 못했다면 만들어내지 않는다. 빈 배열도 정답이다.
- 각 사례: 문제 → 시도 → 증상 → 원인 → 실패한 해결 → 현재 workaround → 최종 해결 → 관련 파일/커밋 → 재현 절차 → 장기 해결. `troubleshootingWorthy`는 "실제 장애 재발 시 도움이 되는가"로 판단한다.
- `id`는 `FAIL001`부터. `sources`에 근거 종류를 표시한다. `discoveredAt`은 `{{today}}`.
- 이미 해결돼 코드에서 사라진 실패도 **기록한다**(과거 보존).

## 입력: Git 이력 발췌

{{gitHistory}}

## 입력: 코드 마커

{{markers}}

## 입력: 설계·보고 문서(경로만, 최하위 근거)

{{docPaths}}

## 입력: 세션 맥락

{{sessionContext}}

출력은 스키마(failures)에 맞춘 JSON 하나다.
