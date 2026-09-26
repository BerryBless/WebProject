using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>같은 내용(sha256)의 첨부를 건드리는 작업(업로드의 행 삽입, 삭제, 청소)을 MySQL 사용자 잠금(<c>GET_LOCK</c>)으로 직렬화한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 잠금 이름은 DB 이름과 내용 SHA-256에서 유도되므로 서로 다른 내용을 잠그는 호출끼리는 경합하지 않는다.
/// 같은 내용을 잠그는 호출끼리는 MySQL 서버가 직렬화한다(호출자의 <see cref="AppDbContext"/> 인스턴스는 여전히 단일 스레드 전용이다).</description></item>
/// <item><description><b>Memory Allocation:</b> 잠금 이름 문자열 1개와 <see cref="Releaser"/> 인스턴스 1개.</description></item>
/// <item><description><b>Blocking:</b> 비동기 대기(Non-blocking), 상한 <see cref="WaitSeconds"/>초 — 넘으면 <c>GET_LOCK</c>이 0을 돌려주고 <see cref="DbLockTimeoutException"/>을 던지며,
/// <c>OverloadExceptionHandler</c>가 503 + Retry-After로 바꾼다(10초 단위라 이 상한 자체는 짧은 대기 상한 오버로드로 테스트한다).</description></item>
/// </list>
/// DB와 파일 시스템은 한 트랜잭션이 아니다. 잠금이 "행 상태 변경 + 파일 조작"을 한 덩어리로 만든다. 세션 단위 사용자 잠금을 쓰는 이유: 트랜잭션 잠금은
/// 커밋에 풀리므로 "행 삭제 커밋 → 파일 삭제"처럼 커밋 뒤까지 이어지는 구간을 잠금 안에 둘 수 없다 — <c>GET_LOCK</c>은 <see cref="IAsyncDisposable.DisposeAsync"/>의 명시적
/// <c>RELEASE_LOCK</c>으로 풀리고, <see cref="HoldAsync(AppDbContext, string, CancellationToken)"/> 이후의 모든 EF 명령이 잠금을 잡은 것과 같은 물리 연결(세션)에서 실행된다
/// (측정: <c>AttachmentIntegrityTests</c>). 잠금 이름은 서버 전역이라 DB 이름 해시로 네임스페이스를 나눈다(<see cref="NameFor"/>, 스펙 D5·R3).
/// <c>RELEASE_LOCK</c>이 실패하는 드문 경우의 동작은 <see cref="Releaser.DisposeAsync"/>의 주석 참조 — 연결이 끊겼으면 세션 종료로 풀리고, 건강한 연결이 풀에 반납되면
/// <b>반납 시점 해제는 보장되지 않지만 늦어도 같은 물리 연결이 다시 대여될 때 <c>ConnectionReset=true</c>의 리셋이 잠금을 푼다</b>
/// (스파이크 S6b: 반납 직후 관측 <c>IS_FREE_LOCK</c> 0, 재대여 후 1).
/// </remarks>
public static class AttachmentLock
{
    /// <summary>MySQL <c>GET_LOCK</c> 이름을 만든다: <c>att:</c> + DB 이름 SHA-256 앞 8자 + <c>:</c> + 내용 SHA 앞 48자(총 61자).</summary>
    /// <param name="databaseName">현재 연결의 DB 이름. 잠금 이름이 서버 전역이라 DB별로 나눈다.</param>
    /// <param name="sha256">첨부 내용의 소문자 16진 SHA-256(64자).</param>
    /// <returns>64자 이하의 잠금 이름.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> UTF-8 바이트 배열·해시 32B·문자열 2개(수십 바이트). 업로드·삭제당 한 번이라 풀링하지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    internal static string NameFor(string databaseName, string sha256)
    {
        var dbTag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(databaseName)))[..8];
        return $"att:{dbTag}:{sha256[..48]}";
    }

    /// <summary>기본 잠금 대기 상한(초). PG 판의 lock_timeout 10초와 같다. <c>GET_LOCK</c> 대기와, 잠금을 쥔 세션의 <c>lock_wait_timeout</c>(메타데이터 잠금 대기)에 함께 쓰인다.
    /// <c>ConnectionStrings:Default</c>의 <c>Default Command Timeout</c>은 0(무한)이거나 이 값보다 커야 한다(<c>StartupValidation</c>).</summary>
    public const int WaitSeconds = 10;

    /// <summary><paramref name="sha256"/> 내용의 사용자 잠금(<c>GET_LOCK</c>)을 기본 대기 상한(<see cref="WaitSeconds"/>초)으로 잡는다. 반환된 핸들을 <c>await using</c>으로 해제해야 잠금이 풀린다.</summary>
    /// <param name="db">잠금을 걸 연결을 제공하는 DbContext. 반환 이후의 모든 EF 명령이 이 잠금과 같은 물리 연결을 쓰게 된다.</param>
    /// <param name="sha256">잠글 내용의 SHA-256(소문자 hex).</param>
    /// <param name="ct">잠금 대기 취소 토큰. 대기 중 취소되면 연결을 닫고 예외를 던진다.</param>
    /// <returns>해제하면 <c>RELEASE_LOCK</c>과 연결 반환을 수행하는 <see cref="IAsyncDisposable"/>.</returns>
    /// <exception cref="DbLockTimeoutException">잠금 대기가 <see cref="WaitSeconds"/>초를 넘었을 때(<c>OverloadExceptionHandler</c>가 503으로 바꾼다).</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출한 스레드에서만 호출한다(<paramref name="db"/>가 단일 스레드 전용이므로). 여러 요청이 동시에 호출해도
    /// 각자 자기 DbContext·연결로 호출하므로 서로 간섭하지 않는다 — 직렬화는 MySQL 서버가 한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 잠금 이름 문자열 1개, <see cref="Releaser"/> 1개. EF의 파라미터화 SQL 실행에 따른 통상적인 할당.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 대기(Non-blocking), 상한 <see cref="WaitSeconds"/>초 뒤 <see cref="DbLockTimeoutException"/>.</description></item>
    /// </list>
    /// </remarks>
    public static Task<IAsyncDisposable> HoldAsync(AppDbContext db, string sha256, CancellationToken ct) => HoldAsync(db, sha256, WaitSeconds, ct);

    /// <summary><paramref name="sha256"/> 내용의 사용자 잠금(<c>GET_LOCK</c>)을 지정한 대기 상한으로 잡는다. 테스트가 짧은 상한으로 타임아웃 경로를 재현할 때 쓴다.</summary>
    /// <param name="db">잠금을 걸 연결을 제공하는 DbContext. 반환 이후의 모든 EF 명령이 이 잠금과 같은 물리 연결을 쓰게 된다.</param>
    /// <param name="sha256">잠글 내용의 SHA-256(소문자 hex).</param>
    /// <param name="waitSeconds">잠금 대기 상한(초). <c>GET_LOCK</c>의 두 번째 인자로 그대로 간다.</param>
    /// <param name="ct">잠금 대기 취소 토큰. 대기 중 취소되면 연결을 닫고 예외를 던진다. 취소 직전에 서버가 잠금을 이미 줬을 수 있으므로
    /// 닫기 전에 <c>DO RELEASE_LOCK</c>을 취소 불가 토큰으로 한 번 보내고(쥐지 않았으면 부작용 없음), 그 실패는 삼킨다.</param>
    /// <returns>해제하면 <c>RELEASE_LOCK</c>과 연결 반환을 수행하는 <see cref="IAsyncDisposable"/>.</returns>
    /// <exception cref="DbLockTimeoutException"><c>GET_LOCK</c>이 1이 아닌 값(0=타임아웃, NULL=오류)을 돌려줬을 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출한 스레드에서만 호출한다(<paramref name="db"/>가 단일 스레드 전용). 직렬화는 MySQL 서버가 한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 잠금 이름 문자열 1개, <see cref="Releaser"/> 1개, 스칼라 조회 1회의 통상적인 할당.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 대기(Non-blocking), 상한 <paramref name="waitSeconds"/>초 뒤 <see cref="DbLockTimeoutException"/>.
    /// 잠금 전에 <c>SET SESSION lock_wait_timeout = </c><paramref name="waitSeconds"/>로 1회 왕복을 더 써서, 잠금을 쥔 세션의 메타데이터 잠금 대기도 같은 상한으로 끊는다
    /// (행 잠금 대기는 <c>innodb_lock_wait_timeout</c> 소관). 세션 값은 다음 대여 때 <c>ConnectionReset=true</c>가 원복한다.
    /// <c>OpenConnectionAsync</c>로 연결을 명시적으로 열어 이후 이 <paramref name="db"/>로 실행하는 모든 명령이 같은 연결에 고정되게 한다
    /// (열어 두지 않으면 EF가 명령마다 풀에서 새로 빌려 잠금을 건 세션과 달라질 수 있다).</description></item>
    /// </list>
    /// </remarks>
    internal static async Task<IAsyncDisposable> HoldAsync(AppDbContext db, string sha256, int waitSeconds, CancellationToken ct)
    {
        // 연결을 명시적으로 열어 둔다: 사용자 잠금은 "잡은 세션"에 묶이므로, 잡은 뒤의 모든 EF 명령이 같은 연결을 써야 한다.
        await db.Database.OpenConnectionAsync(ct);
        string? name = null;
        try
        {
            name = NameFor(db.Database.GetDbConnection().Database, sha256);
            // 잠금을 쥔 세션의 메타데이터 잠금(MDL) 대기에도 같은 상한을 건다(PG 판 lock_timeout 10초와 같은 역할, 스펙 7절).
            // waitSeconds는 int라 파라미터로 보내도 SET SESSION에서 정수로 해석된다(문자열 조립이 아니라 주입 불가).
            // 세션 값은 이 연결이 풀에 반납된 뒤 다음 대여 때 ConnectionReset=true의 리셋이 서버 기본값으로 되돌린다 — 다른 요청에 새지 않는다.
            // 이 값은 MDL 대기만 끊는다. InnoDB 행 잠금 대기는 여전히 innodb_lock_wait_timeout이 정한다.
            await db.Database.ExecuteSqlInterpolatedAsync($"SET SESSION lock_wait_timeout = {waitSeconds}", ct);
            // CAST(... AS SIGNED): GET_LOCK의 반환 타입을 BIGINT로 고정해 long?로 읽는다(1=획득, 0=타임아웃, NULL=오류).
            var acquired = await db.Database.SqlQuery<long?>($"SELECT CAST(GET_LOCK({name}, {waitSeconds}) AS SIGNED) AS `Value`").SingleAsync(ct);
            if (acquired != 1) throw new DbLockTimeoutException($"첨부 잠금 대기 {waitSeconds}초 초과");
        }
        catch
        {
            if (name is not null)
            {
                try
                {
                    // 취소가 GET_LOCK 응답과 경합하면 서버는 이미 잠금을 줬는데 클라이언트는 예외로 빠질 수 있다.
                    // 연결을 닫기 전에 명시적으로 풀어 둔다(쥐지 않았으면 RELEASE_LOCK은 0/NULL을 돌려줄 뿐 부작용이 없다).
                    // 취소된 토큰을 쓰면 이 해제 자체가 즉시 취소되므로 CancellationToken.None을 쓴다.
                    await db.Database.ExecuteSqlInterpolatedAsync($"DO RELEASE_LOCK({name})", CancellationToken.None);
                }
                catch
                {
                    // 해제 실패는 삼킨다: 원래 예외를 가리지 않는다. 연결이 끊겼으면 세션 종료로, 살아 있으면 다음 대여 때 ConnectionReset으로 풀린다.
                }
            }
            await db.Database.CloseConnectionAsync();
            throw;
        }
        return new Releaser(db, name);
    }

    // private sealed class: HoldAsync 내부에서만 만들어지는 구현 세부 사항이라 공개 표면에 노출하지 않는다.
    private sealed class Releaser(AppDbContext db, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                try
                {
                    // 요청이 취소됐어도 잠금은 풀어야 하므로 취소 토큰을 넘기지 않는다.
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT RELEASE_LOCK({name})", CancellationToken.None);
                }
                catch
                {
                    // 해제 실패 시: 연결이 끊겼으면 세션 종료와 함께 잠금도 풀린다. 연결이 살아 있으면 잠금을 쥔 채 풀에 반납될 수 있고,
                    // 늦어도 같은 물리 연결이 다시 대여될 때 ConnectionReset=true의 리셋이 잠금을 푼다
                    // (스파이크 S6b: 반납 직후 관측 IS_FREE_LOCK 0, 재대여 후 1 — 반납 시점 해제는 보장 아님. AttachmentIntegrityTests가 측정).
                    // 그때까지 같은 내용의 요청은 WaitSeconds 뒤 503을 받는다. 다시 던지면 원래 예외를 가리므로 삼킨다.
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
