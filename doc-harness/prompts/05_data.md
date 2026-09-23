# Phase 05a — Data Model Analysis

목표: 프로젝트 전체 관점에서 **데이터 구조**를 다시 분석한다(기능 분석과 별개).

## 분석

- **Database**: 테이블/엔티티, PK, FK, 인덱스, 제약, 트랜잭션 경계, 마이그레이션(파일 목록과 무엇을 바꿨는지 요약).
- **Object**: 엔티티, 모델, DTO(request/response), 설정 객체, 이벤트/메시지. `dtos[].usedBy`는 기능 id.
- 필드는 DbContext/엔티티 클래스/`ModelSnapshot`에서 확인한다. 타입·제약은 코드 그대로.
- DB가 있으면 `diagrams`에 `id: "DATA_ER"`, `type: "er"`, `erDiagram` 하나를 만든다(엔티티가 12개를 넘으면 `DATA_ER_<GROUP>`으로 나눈다). 엔티티 이름은 실제 클래스명.
- 여러 DbContext가 있으면(예: 관리용·읽기 전용) 각각의 역할과 권한 차이를 `transactions`나 `unknowns`에 적는다.

## 입력: DB 관련 파일(먼저 읽을 것)

{{dataFiles}}

## 입력: 코드에서 추출한 DbSet 엔티티

{{entities}}

## 입력: 기능 목록(usedBy 매핑용)

```json
{{features}}
```

출력은 스키마(data)에 맞춘 JSON 하나다.
