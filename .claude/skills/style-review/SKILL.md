---
name: style-review
description: ".NET/C# 스타일(네이밍, 복잡도, 중복, XML 문서화·필수 remarks, 테스트 갭)을 감사해 JSON을 {run_dir}/02_style_findings.json에 출력한다. style-reviewer 전용."
---

# Style Review Skill

## 입력 읽기

1. `{run_dir}/00_input/meta.json`을 읽어 `run_id`, `target_type`, `head_sha`를 확인한다 (`run_dir`은 프롬프트로 전달됨. 없으면 `_workspace/code-review/latest.txt` 참조).
2. `{run_dir}/00_input/diff.txt`를 **처음부터 끝까지** Read로 읽는다. 길면 `offset`/`limit`으로 나눠 읽고, `index.md`가 있으면 파일 위치 탐색에만 쓴다. 요약만으로 판단하지 않는다.
3. diff면 `+` 줄(추가된 코드)에 집중한다. 공개 API (public 멤버)를 특히 꼼꼼히 확인한다.

## 저장소 문맥 조사 (읽기 전용)

| 판정 항목 | 필수 보충 조회 | 조회 불가 시 |
|----------|--------------|------------|
| 테스트 커버리지 갭 | 테스트 파일(`**/*Tests.cs`, `*Spec.cs`, `*Should*.cs`, `*Given*.cs`)에서 대상 메서드명·엔드포인트 경로 Grep | `unverified: 테스트 커버리지` |
| 네이밍·중복 판단 | 같은 프로젝트의 인접 파일 1~2개로 기존 컨벤션 확인 | 표준 C# 컨벤션 기준으로 판정 |
| 프로젝트 주석 규칙 | `CLAUDE.md`의 "인터페이스 및 API 문서화(주석) 규칙" 절 | 아래 4항 기본 규칙 적용 |

`target_type=pr`이면 작업 트리 대신 `git show {head_sha}:<경로>`로 읽는다.

## 감사 체크리스트

### 1. 네이밍 컨벤션 (C# 표준)

| 대상 | 규칙 | 나쁜 예 | 좋은 예 |
|------|------|--------|--------|
| 클래스/인터페이스 | PascalCase | `userService` | `UserService` |
| 인터페이스 | I 접두사 | `UserService` (인터페이스) | `IUserService` |
| 메서드 | PascalCase | `getUser()` | `GetUser()` |
| 지역 변수/파라미터 | camelCase | `UserName` | `userName` |
| 상수/static readonly | PascalCase | `MAX_SIZE`, `maxSize` | `MaxSize` |
| private 필드 | `_` 접두사 camelCase | `userName`, `UserName` | `_userName` |
| async 메서드 | `Async` 접미사 | `GetUser()` | `GetUserAsync()` |

**의미 없는 이름 탐지:**
- 단일 문자 (루프 변수 i/j 외): `a`, `b`, `x`, `temp`, `data`, `obj`
- 타입 반복: `List<User> userList`, `string nameString`

### 2. 메서드 복잡도

- **30줄 초과 메서드**: 분리 필요 신호
- **중첩 4단계 이상**: 보호 절(guard clause)로 평탄화 가능
- **파라미터 5개 이상**: 파라미터 객체(Parameter Object)로 리팩토링 권장

```csharp
// 화살촉 안티패턴 (나쁜 예)
if (condition1)
{
    if (condition2)
    {
        if (condition3)
        {
            // 핵심 로직
        }
    }
}

// 보호 절 (좋은 예)
if (!condition1) return;
if (!condition2) return;
if (!condition3) return;
// 핵심 로직
```

### 3. 코드 품질 지표

**매직 넘버/문자열:**
```csharp
// 나쁜 예
if (status == 2) ...
Thread.Sleep(5000);
var url = "https://api.example.com/v1";

// 좋은 예
if (status == OrderStatus.Confirmed) ...
private const int RetryDelayMs = 5000;
private const string ApiBaseUrl = "https://api.example.com/v1";
```

**죽은 코드:**
- `// TODO`, `// FIXME`, `// HACK` 주석 (기한 없는 것)
- 주석 처리된 코드 블록
- `if (false)` 또는 도달 불가 코드

**에러 처리 패턴:**
```csharp
// 나쁜 예: 빈 catch
try { ... } catch (Exception) { }

// 나쁜 예: 예외 삼키기
catch (Exception ex) { return null; }  // 로깅도 없음

// 나쁜 예: 일관성 없는 패턴 (일부는 예외, 일부는 null 반환, 일부는 bool)
```

### 4. XML 문서화 (프로젝트 규칙 포함)

**4-a. 존재 검사:** 공개(public) 메서드, 클래스, 프로퍼티에 XML 주석이 없으면 보고한다. 자명한 속성(`public string Name { get; set; }`)과 테스트 클래스는 예외.

**4-b. 내용 검사 (CLAUDE.md 필수 항목):** 인터페이스, public 클래스의 메서드, 대리자, RPC 정의에는 `<remarks>`에 아래 세 가지가 모두 있어야 한다. 하나라도 없으면 `medium`으로 보고한다.
- **Thread Safety**: `Thread-safe`/`Not Thread-safe`, 콜백이면 실행 스레드 컨텍스트
- **Memory Allocation**: 힙 할당 여부(`Zero-allocation guaranteed` 또는 할당량), `Span`/`Memory` 버퍼의 소유권·생명주기
- **Blocking 여부**: 즉시 반환 / 동기 블로킹 / 비동기

```csharp
// 부족한 예 (존재하지만 내용 미달 → medium)
/// <summary>패킷을 처리한다.</summary>
bool OnPacketReceived(long sessionId, ReadOnlySpan<byte> packetBuffer);

// 충족 예
/// <summary>수신된 로우 패킷 버퍼를 역직렬화하여 내부 이벤트 파이프라인으로 라우팅합니다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> I/O 스레드 풀에서 직접 호출됩니다. 동기 블로킹 금지.</description></item>
/// <item><description><b>Memory Policy:</b> <paramref name="packetBuffer"/> 소유권은 메서드 실행 동안만 유효합니다.</description></item>
/// <item><description><b>Concurrency:</b> Thread-safe.</description></item>
/// </list>
/// </remarks>
bool OnPacketReceived(long sessionId, ReadOnlySpan<byte> packetBuffer);
```

**4-c. 네트워크·메모리 선언부 인라인 주석:** `Socket`, `Pipe`, `PipeReader/Writer`, `Channel<T>`, `ArrayPool<T>`, `MemoryPool<T>`, `IMemoryOwner<T>`, `Memory<T>`, `Span<T>`, `NetworkStream`, `SocketAsyncEventArgs`, `ValueTask`, `SemaphoreSlim`, `Concurrent*` 타입의 필드·변수·함수 선언에는 **내부 동작 메커니즘을 근거로 한** `//` 주석이 있어야 한다. 없거나 기능 설명만 있으면 `low`로 보고한다.

### 5. 테스트 커버리지 갭

판정 기준은 "diff에 새 테스트가 추가됐는가"가 아니라 **"변경된 공개 동작이 저장소의 어떤 테스트로든 검증되는가"** 다.

1. diff에서 새로 추가되거나 시그니처·동작이 바뀐 public 메서드·엔드포인트를 목록화한다.
2. 테스트 파일에서 메서드명·엔드포인트 경로·클래스명을 Grep한다(저장소 문맥 조사).
3. 어떤 테스트도 참조하지 않으면 갭으로 보고한다. 기존 테스트가 이미 검증하면 보고하지 않는다.
4. 테스트가 있더라도 다음이 빠지면 low로 보고한다: 예외 경로, 경계값(null, 빈 목록, 0, 최대값), 비동기 메서드의 취소·예외

## 심각도 기준

| 심각도 | 기준 |
|--------|------|
| **high** | 명시적 버그 유발 가능 (빈 catch로 오류 은폐), 테스트 없는 복잡 비즈니스 로직 |
| **medium** | 가독성 심각 저해, 30줄+ 메서드, 네이밍 대규모 위반, 프로젝트 필수 `<remarks>` 항목 누락 |
| **low** | 미세 컨벤션 위반, 매직 넘버, XML 주석 부재, 네트워크·메모리 선언부 근거 주석 누락 |

## 점수

`score = max(0, 100 − 10×high − 4×medium − 1×low)` (critical 없음). `counts`는 findings에서 센 값과 일치해야 한다.

## 출력

1. 결과를 `{run_dir}/02_style_findings.json`(또는 오케스트레이터가 지정한 파일명)에 Write 도구로 저장한다. Write는 이 파일에만 사용한다.
2. 발견사항이 없으면 빈 배열, counts 0, score=100으로 저장한다.
3. 저장 후 **최종 응답 첫 줄**에 `{"status":"done","output":"<경로>","counts":{...},"score":N}`을 적고 종료한다. SendMessage는 사용하지 않는다(팀 도구 없음, 리더 ID 미상).
