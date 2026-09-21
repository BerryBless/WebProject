using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>같은 내용(sha256)의 첨부를 건드리는 작업(업로드의 행 삽입, 삭제, 청소)을 PostgreSQL 세션 advisory lock으로 직렬화한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 잠금 키는 <paramref name="sha256"/>에서 유도되므로 서로 다른 내용을 잠그는 호출끼리는 절대 경합하지 않는다.
/// 같은 내용을 잠그는 호출끼리는 PostgreSQL 서버가 직렬화한다(호출자의 <see cref="AppDbContext"/> 인스턴스는 여전히 단일 스레드 전용이다).</description></item>
/// <item><description><b>Memory Allocation:</b> 키 문자열 1개와 <see cref="Releaser"/> 인스턴스 1개.</description></item>
/// <item><description><b>Blocking:</b> 비동기 Non-blocking(대기는 <c>await</c>). 잠금 대기 상한은 세션 <c>lock_timeout='10s'</c>다 — 넘으면 PostgreSQL이
/// SqlState 55P03으로 끊고 <c>OverloadExceptionHandler</c>가 503 + Retry-After로 바꾼다(10초 단위라 이 상한 자체를 테스트로 재현하지는 않는다 — 짧은 타임아웃으로
/// 같은 메커니즘을 별도 테스트에서 측정했다).</description></item>
/// </list>
/// DB와 파일 시스템은 한 트랜잭션이 아니다. 잠금이 "행 상태 변경 + 파일 조작"을 한 덩어리로 만든다. 세션 잠금을 쓰는 이유: 트랜잭션 잠금(<c>pg_advisory_xact_lock</c>)은
/// 커밋에 풀리므로 "행 삭제 커밋 → 파일 삭제"처럼 커밋 뒤까지 이어지는 구간을 잠금 안에 둘 수 없다 — 세션 잠금은 <see cref="IAsyncDisposable.DisposeAsync"/>의 명시적
/// UNLOCK으로 풀리고, <see cref="HoldAsync"/> 이후의 모든 EF 명령이 잠금을 잡은 것과 같은 물리 연결(세션)에서 실행된다(측정:
/// <c>AttachmentIntegrityTests.HoldAsync_KeepsSubsequentEfCommandsOnTheSameSession_AndUnlockReleasesTheKey</c>). UNLOCK이 실패하는 드문 경우의 동작은
/// <see cref="Releaser.DisposeAsync"/>의 주석 참조 — 연결이 깨져 폐기되면 세션 종료로 즉시 풀리고, 건강한 연결이 정상적으로 풀에 반환되면 <b>반환 직후에는
/// 안 풀리지만 그 물리 연결이 다음에 재사용되는 순간(Npgsql이 반환 시 예약해 둔 세션 리셋이 다음 대여자의 첫 명령과 함께 실제로 전송되면서) 풀린다</b>
/// (측정, 아래 참조 — "지연된 리셋"이지 "리셋 범위가 좁다"가 아니다).
/// </remarks>
public static class AttachmentLock
{
    /// <summary>잠금 키 문자열. 다른 용도의 advisory lock과 겹치지 않게 접두사를 둔다. 테스트가 같은 키를 직접 잡는다.</summary>
    internal static string KeyFor(string sha256) => "attachment:" + sha256;

    /// <summary><paramref name="sha256"/> 내용의 세션 advisory lock을 잡는다. 반환된 핸들을 <c>await using</c>으로 해제해야 잠금이 풀린다.</summary>
    /// <param name="db">잠금을 걸 연결을 제공하는 DbContext. 반환 이후의 모든 EF 명령이 이 잠금과 같은 물리 연결을 쓰게 된다.</param>
    /// <param name="sha256">잠글 내용의 SHA-256(소문자 hex).</param>
    /// <param name="ct">잠금 대기 취소 토큰. 대기 중 취소되면 연결을 닫고 예외를 던진다(잠금은 애초에 잡지 못했으므로 풀 것이 없다).</param>
    /// <returns>해제하면 <c>pg_advisory_unlock</c>과 연결 반환을 수행하는 <see cref="IAsyncDisposable"/>.</returns>
    /// <exception cref="Npgsql.PostgresException">잠금 대기가 <c>lock_timeout</c>(10초)을 넘으면 SqlState 55P03.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출한 스레드에서만 호출한다(<paramref name="db"/>가 단일 스레드 전용이므로). 여러 요청이 동시에 호출해도
    /// 각자 자기 DbContext·연결로 호출하므로 서로 간섭하지 않는다 — 직렬화는 PostgreSQL 서버가 한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 잠금 키 문자열 1개, <see cref="Releaser"/> 1개. EF의 파라미터화 SQL 실행에 따른 통상적인 할당.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <c>OpenConnectionAsync</c>는 연결을 명시적으로 열어 이후 이 <paramref name="db"/>로 실행하는
    /// 모든 명령이 같은 연결에 고정되게 한다(열어 두지 않으면 EF가 명령마다 풀에서 새로 빌려 잠금을 건 연결과 달라질 수 있다).</description></item>
    /// </list>
    /// </remarks>
    public static async Task<IAsyncDisposable> HoldAsync(AppDbContext db, string sha256, CancellationToken ct)
    {
        var key = KeyFor(sha256);
        // 연결을 명시적으로 열어 둔다: 세션 잠금은 "잡은 연결"에 묶이므로, 잡은 뒤의 모든 EF 명령이 같은 연결을 써야 한다(열어 두지 않으면 EF가 명령마다 풀에서 새로 빌린다).
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET lock_timeout = '10s'", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({key}, 0))", ct);
        }
        catch
        {
            await db.Database.CloseConnectionAsync();
            throw;
        }
        return new Releaser(db, key);
    }

    // private sealed class: HoldAsync 내부에서만 만들어지는 구현 세부 사항이라 공개 표면에 노출하지 않는다.
    private sealed class Releaser(AppDbContext db, string key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                try
                {
                    // 요청이 취소됐어도 잠금은 풀어야 하므로 취소 토큰을 넘기지 않는다.
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtextextended({key}, 0))", CancellationToken.None);
                }
                catch
                {
                    // UNLOCK 자체가 실패하는 경우는 크게 둘로 갈린다: (a) 연결이 이미 끊겼다 — 이때는 Npgsql이 그 연결을 "깨진 것"으로 표시해
                    // 풀에 돌려주지 않고 실제로 폐기하므로, 그 물리 연결이 물고 있던 PostgreSQL 백엔드 세션도 함께 끝나고 세션이 쥔 advisory lock도
                    // 그때 풀린다(PostgreSQL의 세션 종료 시 잠금 해제 규칙). (b) 연결은 여전히 건강한데 다른 이유로 UNLOCK SQL 자체가 실패했다 —
                    // 이때는 곧이어 부르는 CloseConnectionAsync()가 연결을 "정상 반환"으로 Npgsql 풀에 돌려준다. 측정 결과 이 정상 반환의 순간에는
                    // advisory lock이 아직 안 풀리지만, 리셋 자체가 없는 것이 아니라 지연된다: Npgsql은 반환 시 세션 리셋 SQL을 그 물리 연결의 쓰기
                    // 버퍼에 prepend만 해 두고, 다음 대여자가 그 연결로 보내는 첫 명령과 함께 실제로 전송한다(측정:
                    // AttachmentIntegrityTests.ClosingAPooledConnection_WithoutAnExplicitUnlock_DelaysReleaseUntilThePhysicalConnectionIsNextUsed —
                    // 같은 pg_backend_pid()로 재사용된 세션은 그 시점에 advisory lock 수가 0이 되고, 다른 세션의 pg_try_advisory_lock도 그때 성공한다).
                    // (b) 경로에서는 이 잠금 키에 대한 이후 요청이, 이 물리 연결이 다음에 재사용되는 순간(또는 연결이 다시 깨지거나 Npgsql의 유휴
                    // 연결 수명이 지나 실제로 폐기될 때)까지 lock_timeout(10초) 뒤 503을 받는 형태로 잠기지만, 다른 내용(sha256)에는 영향이 없고
                    // 풀이 계속 쓰이는 한(이 물리 연결이 언젠가 다른 요청에 다시 빌려짐) 스스로 회복된다 — 그래도 여기서 다시 던지면
                    // 바깥의 원래 예외(업로드·삭제 본문에서 난 것)를 가릴 뿐 이 잔여 위험을 없애지 못하므로, 원래 예외를 보존하기 위해 삼킨다.
                    // HoldAsync는 로거를 받지 않는(브리프가 정한 시그니처) 정적 유틸리티라 별도로 남길 곳이 없다 — 아래 finally가 연결은 어떤 경우에도 반환한다.
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
