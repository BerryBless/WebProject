---
name: security-review
description: ".NET/C# 보안 취약점(OWASP Top 10·CWE: 인젝션·인증·민감정보·CSRF·권한)을 스캔해 JSON을 {run_dir}/02_security_findings.json에 출력한다. security-reviewer 전용."
---

# Security Review Skill

## 입력 읽기

1. `{run_dir}/00_input/meta.json`을 읽어 `run_id`, `target_type`, `head_sha`를 확인한다 (`run_dir`은 프롬프트로 전달됨. 없으면 `_workspace/code-review/latest.txt` 참조).
2. `{run_dir}/00_input/diff.txt`를 **처음부터 끝까지** Read로 읽는다. 길면 `offset`/`limit`으로 나눠 읽고, `index.md`가 있으면 파일 위치 탐색에만 쓴다. 요약만으로 판단하지 않는다.
3. `+` 줄(추가된 코드)과 **`-` 줄(삭제된 코드) 모두** 검사한다:
   - 추가: 새 취약점 도입 여부
   - 삭제: **보안 통제 제거** 여부 — `[Authorize]`/`[ValidateAntiForgeryToken]`/역할 검사/소유권 검사/입력 검증/`RequireHttps`/CORS 제한이 사라졌는데 대체 통제가 추가되지 않았으면 결함이다. 삭제만 있는 변경도 취약점을 만든다.
   - 시크릿/자격증명: 추가·삭제 모두 확인(삭제되었어도 이력에 남아 있으면 회전 권고)

## 저장소 문맥 조사 (읽기 전용)

| 판정 항목 | 필수 보충 조회 | 조회 불가 시 |
|----------|--------------|------------|
| 삭제된 통제의 대체 여부 | 전역 인증 정책(`Program.cs`의 `AddAuthorization`/`FallbackPolicy`, 미들웨어 순서), 엔드포인트 그룹의 `RequireAuthorization` | 결함으로 보고하되 `detail`에 "전역 정책 미확인" 명시 |
| 취약 의존성 | `dotnet list package --vulnerable --include-transitive` (Bash) | `unverified: 취약 의존성` + 사유 |
| CORS/쿠키/HTTPS 설정 | `appsettings*.json`, `Program.cs` | `unverified` |

`target_type=pr`이면 작업 트리 대신 `git show {head_sha}:<경로>`로 읽는다.

## 감사 체크리스트

### 1. 인젝션 (CWE-89, 77, 90)

**SQL 인젝션 패턴:**
```csharp
// 위험: 문자열 연결
$"SELECT * FROM Users WHERE Name = '{name}'"
// 위험: string.Format
string.Format("SELECT ... WHERE id = {0}", id)
// 안전: 파라미터화
"SELECT * FROM Users WHERE Name = @name"
```

**커맨드 인젝션 패턴:**
- `Process.Start()` 또는 `ProcessStartInfo`에 사용자 입력 전달
- `cmd.exe /c` + 사용자 입력 연결

**EF Core 주의:**
- `FromSqlRaw()`에 보간 문자열 사용 → `FromSqlInterpolated()` 또는 파라미터 사용 필요

### 2. 민감 정보 노출 (CWE-312, 798, 532)

- 소스 코드 내 비밀번호, API 키, 연결 문자열 하드코딩
- `Console.WriteLine` / `_logger.LogInformation`에 비밀번호, 토큰, PII 기록
- `ToString()` 또는 직렬화 시 민감 필드 노출
- `[JsonIgnore]` / `[Newtonsoft.Json.JsonIgnore]` 누락된 민감 속성

### 3. 인증·권한 결함 (CWE-306, 862, 639)

- `[Authorize]` 어트리뷰트 누락된 컨트롤러/액션, `RequireAuthorization()` 없는 최소 API 엔드포인트
- 사용자 ID를 요청 파라미터에서 직접 받아 DB 조회 (IDOR)
- JWT/쿠키 유효성 검사 우회 가능 코드
- 역할(Role) 검사 없이 관리 기능 접근 허용
- **diff에서 위 통제가 제거된 경우** (입력 읽기 3항)

### 4. 역직렬화 (CWE-502)

```csharp
// 위험
BinaryFormatter.Deserialize(stream)
JsonConvert.DeserializeObject(json, Type.GetType(typeName))  // 타입 제어 안 됨

// 위험: TypeNameHandling
new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto }
```

### 5. 암호화 결함 (CWE-327, 759)

- `MD5` / `SHA1` 단독 사용 (비밀번호 해싱 등)
- 하드코딩된 IV, Salt, 암호화 키
- `Random` 클래스를 보안 목적으로 사용 (→ `RandomNumberGenerator` 사용 필요)
- ECB 모드 사용

### 6. XSS / CSRF (CWE-79, 352)

- Razor 뷰에서 `Html.Raw()` 또는 `@:` + 사용자 입력
- `[ValidateAntiForgeryToken]` 누락된 POST 엔드포인트
- CORS 와일드카드 (`*`) 설정, `AllowAnyOrigin()` + `AllowCredentials()` 조합

## 심각도 기준

| 심각도 | 기준 |
|--------|------|
| **critical** | 즉각적 익스플로잇 가능 (SQL 인젝션, 하드코딩 자격증명, 인증 우회, 인증 통제 삭제) |
| **high** | 익스플로잇에 조건 필요하지만 데이터 손실/권한 탈취 가능 |
| **medium** | 정보 노출, 약한 암호화, 조건부 CSRF |
| **low** | 보안 모범 사례 미준수, 잠재적 위험 |

## 점수

`score = max(0, 100 − 25×critical − 10×high − 4×medium − 1×low)`. `counts`는 findings에서 센 값과 일치해야 한다.

## 출력

1. 결과를 `{run_dir}/02_security_findings.json`(또는 오케스트레이터가 지정한 파일명)에 Write 도구로 저장한다. Write는 이 파일에만 사용한다.
2. 발견사항이 없으면 빈 배열, counts 0, score=100으로 저장한다.
3. 저장 후 **최종 응답 첫 줄**에 `{"status":"done","output":"<경로>","counts":{...},"score":N}`을 적고 종료한다. SendMessage는 사용하지 않는다(팀 도구 없음, 리더 ID 미상).
