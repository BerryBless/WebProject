using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>단일 작성자 비밀번호의 검증기. 서버에는 해시만 있다(<c>Admin:PasswordHash</c>).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 생성 후 불변. <see cref="PasswordHasher{TUser}"/>는 무상태다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Verify"/>는 호출당 수백 바이트(디코딩 버퍼·파생 키)를 할당한다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="Verify"/>는 CPU 바운드 동기 연산(PBKDF2-HMAC-SHA512 10만 회, 수십 ms). 호출부가 동시 실행 수를 속도 제한기로 묶는다.</description></item>
/// </list>
/// 프레임워크 내장 해셔를 쓰는 이유: 추가 패키지(공급망 표면) 없이 솔트·반복 횟수·알고리즘 버전이 해시 문자열에 함께 저장되고, 비교가 고정 시간이다.
/// </remarks>
public sealed class AdminCredential
{
    // PasswordHasher<T>: 내부적으로 상태를 갖지 않고 매 호출마다 해시 문자열에 저장된 포맷 마커·반복 횟수를 읽어
    // RFC 2898(PBKDF2) 파생을 수행하는 순수 계산기라 정적 인스턴스 하나를 프로세스 전체가 공유해도 안전하다.
    private static readonly PasswordHasher<object> Hasher = new();
    // 제네릭 사용자 타입 매개변수가 필요할 뿐 실제로 값을 읽지 않으므로 더미 인스턴스 하나만 있으면 된다.
    private static readonly object User = new();
    private readonly string _hash;

    /// <summary><c>Admin:PasswordHash</c> 설정을 읽어 자격 증명기를 만든다.</summary>
    /// <param name="options">지연 바인딩된 <see cref="AdminOptions"/>.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> DI 컨테이너가 앱 시작 시 1회 호출한다. 생성 후 모든 필드가 불변이라 이후 여러 요청 스레드가 동시에 <see cref="Verify"/>를 호출해도 안전하다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="Fingerprint"/> 계산을 위한 SHA-256 다이제스트 버퍼(32바이트)와 16진 문자열 1개를 생성 시 1회 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. SHA-256 해시 1회 계산은 PBKDF2보다 수천 배 빠르므로 시작 지연에 영향이 없다.</description></item>
    /// </list>
    /// </remarks>
    public AdminCredential(IOptions<AdminOptions> options)
    {
        _hash = options.Value.PasswordHash;
        // 지문: 해시 문자열의 SHA-256 앞 16바이트. 쿠키 티켓에 넣어 "비밀번호가 바뀌면 기존 세션 무효"를 DB 상태 없이 구현한다. 해시 자체는 쿠키에 넣지 않는다.
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_hash)).AsSpan(0, 16));
    }

    /// <summary>현재 <c>Admin:PasswordHash</c>를 식별하는 16바이트(32자 hex) 지문. 해시 원문을 복원할 수 없다.</summary>
    public string Fingerprint { get; }

    /// <summary>평문 비밀번호가 설정된 해시와 일치하는지 검증한다.</summary>
    /// <param name="password">사용자가 입력한 평문 비밀번호.</param>
    /// <returns>일치하면 <c>true</c>. 해시가 비어 있으면(설정 누락) 항상 <c>false</c>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="Hasher"/>가 무상태이고 <see cref="_hash"/>가 불변이라 여러 요청 스레드가 동시에 호출해도 안전하다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 호출당 base64 디코딩 버퍼·PBKDF2 파생 키(수백 바이트)를 할당한다. 반환 후 GC 대상이 되며 별도로 보관하지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> CPU 바운드 동기 블로킹(PBKDF2-HMAC-SHA512 10만 회 반복, 수십 ms). 호출자는 요청 스레드를 이 시간만큼 점유한다 — 동시 로그인 시도 수는 상위 계층의 속도 제한기(<c>LoginConcurrency</c>)로 묶는다.</description></item>
    /// </list>
    /// </remarks>
    public bool Verify(string password)
    {
        if (_hash.Length == 0)
        {
            return false; // 해시 미설정 = 로그인 불가(fail closed)
        }
        return Hasher.VerifyHashedPassword(User, _hash, password) != PasswordVerificationResult.Failed;
    }

    /// <summary>평문 비밀번호를 PBKDF2로 해싱한다. <c>hash-password</c> CLI와 테스트가 <c>Admin:PasswordHash</c> 설정 값을 만들 때 쓴다.</summary>
    /// <param name="password">해싱할 평문 비밀번호.</param>
    /// <returns>솔트·반복 횟수·알고리즘 버전을 함께 담은 base64 해시 문자열.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="Hasher"/>가 무상태라 여러 스레드가 동시에 호출해도 안전하다(정적 메서드이므로 인스턴스 상태에 의존하지 않는다).</description></item>
    /// <item><description><b>Memory Allocation:</b> 매 호출마다 새 무작위 솔트(16바이트)·파생 키·결과 문자열을 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> CPU 바운드 동기 블로킹(PBKDF2-HMAC-SHA512 10만 회 반복, 수십 ms). CLI·테스트 셋업에서만 호출되며 요청 처리 경로에는 없다.</description></item>
    /// </list>
    /// </remarks>
    public static string Hash(string password) => Hasher.HashPassword(User, password);
}
