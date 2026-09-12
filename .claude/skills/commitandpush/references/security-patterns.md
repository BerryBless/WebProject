# 민감 정보 탐지 패턴 레퍼런스 (정본)

`git-security-auditor`와 Stop 훅(`scripts/auto-commit.ps1`)이 공유하는 기준이다. 에이전트 정의에는 패턴을 복제하지 않고 이 파일만 참조한다. 훅은 이 중 "훅 적용" 표시된 부분집합을 코드로 갖는다.

## 1. 금지 파일 — 이름/경로 기준 (훅 적용)

정규식(경로 전체, 대소문자 무시):
```regex
(^|/)\.env(\.[^/]+)?$                    # .env, .env.local, .env.production …
\.(pem|key|p12|pfx|jks|ppk)$             # 키·인증서 컨테이너
(^|/)id_(rsa|ed25519|ecdsa|dsa)$         # SSH 개인키 (공개키 .pub 은 대상 아님)
(^|/)(credentials|secrets)(\.(json|ya?ml))?$
(^|/)service-account\.json$
(^|/)appsettings\.(Production|Staging)\.json$   # 운영 설정은 내용과 무관하게 검토 대상(WARN 이상)
```
**예외(파일별로만 적용):** `\.(example|sample|template)$` — 이 파일 **자신**만 면제된다. 같은 커밋에 `.env.example`이 있다고 해서 `.env`가 면제되지 않는다.
**대상 아님:** `*.cer`, `*.crt`(공개 인증서. 단 내용에 PRIVATE KEY가 있으면 2절에서 걸림), `*.pub`.

## 2. 금지 콘텐츠 패턴 (diff `+` 줄 스캔, 훅 적용은 ★)

### 개인키 ★
```regex
-----BEGIN [A-Z ]*PRIVATE KEY( BLOCK)?-----      # RSA/EC/OPENSSH/DSA/PGP/ENCRYPTED 모두 포함
```
### 클라우드·SaaS 키 ★
```regex
AKIA[0-9A-Z]{16}    ASIA[0-9A-Z]{16}                          # AWS
ghp_[0-9A-Za-z]{36}  gho_[0-9A-Za-z]{36}  github_pat_[0-9A-Za-z_]{40,}   # GitHub
glpat-[0-9A-Za-z\-]{20}                                       # GitLab
sk_(live|test)_[0-9A-Za-z]{24}                                # Stripe
xox[baprs]-[0-9A-Za-z\-]{10,}                                 # Slack
AIza[0-9A-Za-z\-_]{35}   ya29\.[0-9A-Za-z\-_]+                # Google
```
Twilio(`AC…`/`SK…` 32 hex)는 16진 해시와 구분이 안 되므로 **`twilio|account_sid|auth_token` 문맥이 같은 줄에 있을 때만** 보고한다.

### JWT ★
```regex
eyJ[A-Za-z0-9\-_]{10,}\.eyJ[A-Za-z0-9\-_]{10,}\.[A-Za-z0-9\-_]{10,}   # 헤더·페이로드 모두 eyJ 로 시작하는 3세그먼트
```

### 키 이름 기반 값 (JSON·C#·YAML·INI 공통) ★
키 이름 앞뒤의 따옴표를 허용한다. 이것이 없으면 ASP.NET Core `appsettings*.json`의 `"Password": "…"`를 놓친다.
```regex
(?i)["']?(password|passwd|pwd|secret|client[_-]?secret|api[_-]?key|apikey|access[_-]?token|auth[_-]?token|bearer[_-]?token|private[_-]?key)["']?\s*[=:]\s*["'][^"'${}<>%]{8,}["']
```

### 연결 문자열 ★
키 순서에 의존하지 않는다(`Server=…;Password=` 순서 강제 금지).
```regex
(?i)(Password|Pwd|AccountKey|SharedAccessKey)=[^;"'\s]{6,}            # SQL Server/Npgsql/MySQL/Azure Storage/Service Bus
[a-z][a-z0-9+.\-]*://[^/\s:@"']+:[^@\s"']{4,}@                          # URL 자격증명 (postgres://user:pw@, redis://:pw@, amqp://)
```

### Azure/GCP 환경 변수
```regex
(?i)(AZURE|GCP|GOOGLE)_[A-Z_]*(KEY|SECRET|TOKEN|PASSWORD)\s*[=:]\s*\S{8,}
```
GUID 단독 패턴은 **사용하지 않는다** (`.sln`/`.csproj`의 모든 GUID에 걸린다). GUID는 위 키 이름 패턴의 값으로 잡힐 때만 의미가 있다.

## 3. 플레이스홀더 예외 (값 기준, 훅 적용)

값이 다음에 해당하면 매치를 무시한다:
```regex
(?i)your[_-]?|replace|example|placeholder|changeme|dummy|sample|<[^>]+>|\*{3,}|x{6,}|\$\{|\$\(|%[A-Z_]+%
```
예: `"<REPLACE_ME>"`, `"${API_KEY}"`, `"your-password-here"`, `"xxxxxxxx"`, `%DB_PASSWORD%`.

## 4. 문맥별 처리 (파일명·주석 여부로 실제 비밀값을 면제하지 않는다)

| 문맥 | 처리 | 이유 |
|------|------|------|
| 실제 비밀값이 **주석** 안에 있음 | 값 기준 심각도 그대로 (FAIL) | Git 이력에 남는 노출은 주석이든 아니든 같다 |
| 실제 비밀값이 `*.example`/`*.sample` 안에 있음 | 값 기준 심각도 그대로 (FAIL) | 예시 파일도 공개된다. 1절 면제는 "파일 이름 자체"에만 해당 |
| 테스트 경로(`*Tests*/`, `*.Tests/`, `test/`, `fixtures/`) + 플레이스홀더로 판단 가능한 값 | WARN | 픽스처일 가능성. 사람이 확인 |
| 테스트 경로 + 플레이스홀더 패턴 불일치 + 고엔트로피 | FAIL | 실제 키를 픽스처로 쓰는 사고 |
| 이미 `.gitignore`에 있으나 강제 추가(`git add -f`)된 파일 | FAIL | 의도적 우회 |
| 삭제 줄(`-`)에만 있는 비밀값 | LOW(정보) + "이력에 남아 있으므로 회전 권고" | 이번 커밋의 새 노출은 아님 |

## 5. 심각도와 판정

| 심각도 | 유형 | 판정 |
|--------|------|------|
| CRITICAL | 개인키, 클라우드 액세스 키, 비밀번호·시크릿 실제값, URL 자격증명 | FAIL |
| HIGH | API 키·OAuth·JWT 실제값, 연결 문자열 비밀번호, 1절 금지 파일(`.env`, 키 파일, `secrets.json`) | FAIL |
| MEDIUM | 운영 설정 파일(`appsettings.Production.json`) 추가, 테스트 픽스처 의심 값, 내부 호스트명·IP 대량 노출 | WARN → 사용자 확인 |
| LOW | 이메일 주소(봇 서명·작성자 제외), 키 **이름**만 있고 값 없음, 삭제 줄의 비밀값 | PASS(로그 기재) |

- 판정은 세 값뿐이다: **PASS**(CRITICAL/HIGH/MEDIUM 없음), **WARN**(MEDIUM만), **FAIL**(CRITICAL 또는 HIGH 1건 이상).
- 불확실하면 한 단계 위로 올린다. 단 "불확실"의 근거를 결과 파일에 적는다.

## 6. 스캔 방법 (에이전트용)

Bash 출력은 약 30,000자에서 잘리므로 diff 전체를 출력하지 않는다.
```bash
git diff --staged --name-only                                   # 파일 목록
git ls-files --others --exclude-standard                        # 미추적(스테이지 전) 파일 목록 — 이름 검사 대상
git diff --staged -U0 --no-color -- "<file>" | grep -nE '^\+[^+]' | grep -iE -f <패턴파일>   # 파일별 매치 줄만
```
결과 파일에 `scanned_files`, `scanned_added_lines`를 기록한다. 스캔하지 못한 파일(바이너리·크기 초과)은 "미검사"로 나열하고, 미검사 파일이 1절에 해당하면 FAIL이다.
