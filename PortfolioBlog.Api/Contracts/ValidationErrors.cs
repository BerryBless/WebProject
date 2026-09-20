namespace PortfolioBlog.Api.Contracts;

/// <summary>필드별 검증 오류를 모아 <c>TypedResults.ValidationProblem</c>에 넘길 사전을 만든다. 필드 키는 JSON 속성명(camelCase)을 쓴다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 요청 처리 중 단일 스레드에서 생성·누적·소비되는 단명 값 객체다.</description></item>
/// <item><description><b>Memory Allocation:</b> 필드마다 <see cref="List{T}"/> 1개, <see cref="ToDictionary"/> 호출 시 배열·사전을 새로 할당한다. 오류가 없으면 내부 사전 외 추가 할당이 없다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class ValidationErrors
{
    // Dictionary<string, List<string>>: 필드별로 오류 메시지를 순서대로 누적하는 용도라 정렬·중복 제거가 필요 없다.
    // Ordinal 비교자: 필드 키가 코드에 박힌 camelCase 리터럴이라 문화권별 대소문자 규칙이 끼어들 이유가 없다.
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    /// <summary>지금까지 하나 이상의 필드에 오류가 추가되었는지.</summary>
    public bool Any => _errors.Count > 0;

    /// <summary>필드에 오류 메시지 하나를 추가한다.</summary>
    /// <param name="field">오류가 발생한 요청 필드의 camelCase 이름.</param>
    /// <param name="message">사용자에게 보여줄 오류 메시지.</param>
    /// <returns>체이닝을 위한 자기 자신.</returns>
    public ValidationErrors Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) { list = new List<string>(1); _errors[field] = list; }
        list.Add(message);
        return this;
    }

    /// <summary><c>TypedResults.ValidationProblem</c>에 바로 넘길 수 있는 필드→메시지 배열 사전으로 변환한다.</summary>
    /// <returns>필드별 오류 메시지 배열 사전.</returns>
    public Dictionary<string, string[]> ToDictionary() => _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
}
