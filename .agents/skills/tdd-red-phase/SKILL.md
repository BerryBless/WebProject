---
name: tdd-red-phase
description: "TDD Red 단계: 요구사항에서 실패하는 xUnit 테스트와 컴파일 가능한 스텁을 설계하고 dotnet test(trx)로 '빌드 성공·전원 실패'를 증빙한다. tdd-analyst 전용."
---

# TDD Red Phase Skill

## 입력 읽기
1. `{run_dir}/00_requirements{_cN}.md`
2. 누적 사이클이면 `{run_dir}/01_analyst/Tests/`, `{run_dir}/02_builder/Src/`(승격된 구현), `00_manifest.json`

## 요구사항 분해
- "무엇을 **해야** 하는가" → Happy Path / "비정상 입력" → Error / "경계값(0, null, 빈 값, 최대·최소)" → Edge / 필요 시 Concurrency
- 확정할 수 없는 항목은 `open_questions`로(추측 확정 금지)

## 테스트 템플릿 (네임스페이스 계약 준수)
```csharp
namespace TddSession.Tests;          // Xunit 은 csproj 의 global using. 구현은 부모 네임스페이스 TddSession 에 있어 using 불필요

public class CalculatorTests
{
    // ─── Happy Path ───
    [Fact]
    public void Add_TwoPositives_ReturnsSum()
    {
        var sut = new Calculator();          // Arrange
        var result = sut.Add(2, 3);          // Act
        Assert.Equal(5, result);             // Assert
    }

    // ─── Edge Cases ───  (여러 값은 Theory — xUnit 2.9 에는 Assert.Multiple 이 없다)
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(int.MaxValue, 0, int.MaxValue)]
    public void Add_Boundaries_ReturnsExpected(int a, int b, int expected)
        => Assert.Equal(expected, new Calculator().Add(a, b));

    // ─── Error Cases ───
    [Fact]
    public void Divide_ByZero_ThrowsDivideByZeroException()
        => Assert.Throws<DivideByZeroException>(() => new Calculator().Divide(1, 0));
}
```

## 스텁 템플릿 (컴파일 통과용, 새 타입만 `01_analyst/Src/`)
```csharp
namespace TddSession;

/// <summary>[STUB] 테스트 컴파일용 — 구현 없음. Green 단계에서 같은 파일명으로 02_builder/Src 에 구현된다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 구현 시 확정.</description></item>
/// <item><description><b>Memory Allocation:</b> 구현 시 확정.</description></item>
/// <item><description><b>Blocking:</b> 구현 시 확정.</description></item>
/// </list>
/// </remarks>
public class Calculator
{
    public int Add(int a, int b) => throw new NotImplementedException();
    public int Divide(int a, int b) => throw new NotImplementedException();
}
```
**누적 사이클 규칙:** 타입이 이미 `02_builder/Src/<Feature>.cs`에 있으면 `01_analyst/Src`에 같은 파일을 쓰지 않는다(파일 우선순위에 가려져 무시된다). 새 멤버 서명은 그 builder 파일에 `throw new NotImplementedException();`으로 추가한다.

## Red 증빙 (필수)
```bash
dotnet test "$run_dir/TddSession.csproj" --nologo --logger "trx;LogFileName=red_attempt$N.trx" --results-directory "$run_dir/01_analyst/results" > "$run_dir/01_analyst/test_results_attempt$N.txt" 2>&1; echo "exit=$?" >> "$run_dir/01_analyst/test_results_attempt$N.txt"
```
- trx `<Counters total= passed= failed=>`로 판정: **빌드 성공**, 새 테스트 `failed == new_tests`, 기존 회귀 테스트는 `passed`
- 빌드 실패면 스텁 서명을 고쳐 1회 재시도. 새 테스트가 통과하면 설계 오류(구현 없이 통과) → 수정
- 결과를 `test_design.md`에 기록

## test_design.md
```markdown
# 테스트 설계 근거 (cycle N)
## 동작 목록 | # | 동작 | 테스트 | 유형(Happy/Edge/Error) |
## 설계 결정: 인터페이스, 제외한 테스트(이유), open_questions
## Red 증빙: 빌드 성공 / 새 테스트 N개 전원 실패 / 회귀 N개 통과 / trx 경로
```

## 좋은 테스트 vs 나쁜 테스트
| 좋은 | 나쁜 |
|---|---|
| `Add_TwoPositives_ReturnsSum` | `Test1` |
| 하나의 동작 | 여러 동작 혼합 |
| `Assert.Equal(5, r)` | `Assert.True(r > 0)` |
| `Assert.Throws<T>` | try/catch 검증 |

기능당 3~7개로 시작(Happy 1~2, Edge 1~2, Error 1~2).

## 출력
1. `{run_dir}/01_analyst/Tests/<Feature>Tests.cs`, `{run_dir}/01_analyst/Src/<Feature>.cs`(새 타입만), `test_design.md`, `results/red_attemptN.trx`. Write는 `01_analyst/`에만(누적 서명 추가만 예외)
2. 최종 응답 첫 줄 `{"status":"done|error","tests_file":"…","stub_files":[…],"build_ok":true,"total":N,"new_tests":N,"new_failed":N,"regression_passed":N,"trx":"…","open_questions":[…]}`. SendMessage 사용 금지(builder에게 신호를 보내지 않는다)
