namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>설정 섹션 <c>Admin</c>.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 프로퍼티는 public set을 가지지만, 싱글턴으로 등록되어 프로그램 시작 후 수정되지 않는다. 읽기만 Thread-safe.</description></item>
/// <item><description><b>Memory Allocation:</b> 프로퍼티는 문자열 참조만 저장.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class AdminOptions
{
    /// <summary>설정 섹션 이름.</summary>
    public const string SectionName = "Admin";

    /// <summary>공백 구분 CIDR. 비어 있으면 아무도 관리 표면에 접근할 수 없다(안전 기본값).</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 문자열은 불변이므로 Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> 할당 없음. 참조만 반환.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public string AllowedCidrs { get; set; } = string.Empty;

    /// <summary><c>hash-password</c> 명령이 출력한 PBKDF2 해시. 비어 있으면 로그인은 항상 실패한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 문자열은 불변이므로 Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> 할당 없음. 참조만 반환.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>분당 IP별 로그인 시도 제한 횟수. 기본값 5.</summary>
    public int LoginPerIpPerMinute { get; set; } = 5;

    /// <summary>분당 전체 로그인 시도 제한 횟수. 기본값 20.</summary>
    public int LoginGlobalPerMinute { get; set; } = 20;

    /// <summary>동시에 실행할 수 있는 해시 검증 수(PBKDF2는 CPU 바운드). 기본값 2.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정수는 스레드 안전하게 읽힌다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public int LoginConcurrency { get; set; } = 2;

    /// <summary>세션 절대 수명(시간). sliding 연장 없음. 기본값 12.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정수는 스레드 안전하게 읽힌다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public int SessionHours { get; set; } = 12;
}
