---
name: tdd-green-phase
description: "TDD Green 단계: 실패하는 테스트를 Fake It → Obvious → Triangulation 순으로 최소 코드로 통과시킨다. Gold Plating 금지, 프로젝트 주석 규칙 필수. tdd-builder 전용."
---

# TDD Green Phase Skill

## 입력 읽기
1. `{run_dir}/01_analyst/Tests/*.cs` — 실패하는 테스트
2. `{run_dir}/01_analyst/Src/*.cs` — 스텁(새 타입) 또는 `02_builder/Src/*.cs`(누적 사이클의 기존 구현 + 새 서명 스텁)
3. 재작업이면 프롬프트의 실패 테스트·오류 원문

## 구현 전략 (우선순위)
1. **Fake It** — 테스트가 1개면 상수 반환으로 즉시 Green
2. **Obvious Implementation** — 로직이 자명하면 바로 작성
3. **Triangulation** — 테스트가 늘어 상수가 불가능해지면 일반화

## 금지 (현재 테스트에 없으면 작성 금지)
```csharp
public int Add(int? a, int? b) => (a ?? 0) + (b ?? 0);          // ❌ 테스트에 없는 null 처리
public int Add(int a, int b) => checked(a + b);                 // ❌ 테스트에 없는 오버플로우 정책
// ❌ 테스트에 없는 캐싱 (예시는 실제 API: ConcurrentDictionary.GetOrAdd)
private readonly ConcurrentDictionary<(int, int), int> _cache = new();
public int Add(int a, int b) => _cache.GetOrAdd((a, b), static k => k.Item1 + k.Item2);
public interface ICalculator { int Add(int a, int b); }          // ❌ 미래 확장용 인터페이스
```

## 필수 (Gold Plating 이 아니다 — CLAUDE.md 규칙)
```csharp
namespace TddSession;

/// <summary>두 정수의 합과 몫을 계산한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 상태가 없는 순수 함수.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation guaranteed.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(동기, I/O 없음).</description></item>
/// </list>
/// </remarks>
public class Calculator
{
    public int Add(int a, int b) => a + b;                       // Green: 가장 단순한 구현
    public int Divide(int a, int b) => a / b;                    // b==0 이면 런타임이 DivideByZeroException 을 던진다 — 별도 검사는 테스트가 요구할 때만
}
```
메모리·네트워크 타입(`ArrayPool`, `Span`, `Channel`, `SemaphoreSlim` …)을 선언하면 내부 동작 근거 `//` 주석을 단다.

## 파일 규칙
- 스텁과 **같은 파일명·네임스페이스(`TddSession`)** 로 `{run_dir}/02_builder/Src/`에 쓴다(파일 우선순위로 스텁을 덮음)
- 재작업이면 기존 파일을 Edit로 최소 수정
- 작성 후 `dotnet build "{run_dir}/TddSession.csproj" --nologo -v q`로 컴파일 확인. 가능하면 `dotnet test`도(결과는 자가 확인용, 판정은 qa)

## build_notes.md (시도별 누적)
```markdown
# Green Phase 구현 노트
## Attempt N
- 전략: Fake It / Obvious / Triangulation — 이유
- 구현 결정: | 테스트 | 선택 | 이유 |
- Gold Plating 거부: …
- (재작업) 실패 테스트 → 수정 내용
```

## 출력
1. `{run_dir}/02_builder/Src/<Feature>.cs`, `{run_dir}/02_builder/build_notes.md`. Write는 `02_builder/`에만
2. 최종 응답 첫 줄 `{"status":"done|error","attempt":N,"files":[…],"strategy":"…","build_ok":true,"self_test":{"ran":true,"passed":N,"failed":N},"notes":"…"}`. SendMessage 사용 금지(qa에게 검증 요청을 보내지 않는다)
