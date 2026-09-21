using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Storage;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>같은 내용의 삭제·업로드가 하나의 잠금으로 직렬화되는지, 그 잠금 메커니즘(같은 세션 고정·명시적 해제·<c>lock_timeout</c>·풀 반환) 자체가
/// 측정한 그대로 동작하는지 검증한다. 테스트가 그 잠금을 직접 쥐고 요청이 기다리는 것을 본다(경쟁을 확률에 맡기지 않는다).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 시작하고, <see cref="HttpClient"/> 요청·<see cref="NpgsqlConnection"/> 왕복은 각각 자신의 비동기 흐름으로 진행된다. 여러 테스트가 같은 <see cref="PostgresContainerFixture"/> 컨테이너를 공유하고, <see cref="DeleteAndUpload_OfTheSameContent_WaitForTheContentLock_AndReleaseIt"/>·<see cref="InterleavedDeleteAndReupload_NeverLeavesARowWithoutItsFile"/>·<see cref="Upload_WhenFileVanishesWhileWaitingForTheLock_ReSavesItFromTheReopenedFormFile"/> 세 테스트는 같은 픽스처(<see cref="Png"/>)를 올리므로 advisory lock 키가 <b>같다</b> — 그래도 서로 간섭하지 않는 이유는 테스트마다 격리된 <see cref="ApiFactory"/>가 각자 별도의 PostgreSQL 데이터베이스를 쓰기 때문이다(advisory lock은 데이터베이스 범위라 다른 DB의 같은 키는 서로 다른 잠금이다).</description></item>
/// <item><description><b>Memory Policy:</b> 테스트마다 격리된 <see cref="ApiFactory"/>(자체 DB + 자체 임시 첨부 폴더)를 쓴다.</description></item>
/// <item><description><b>Blocking:</b> 비동기. 실제 PostgreSQL 컨테이너를 쓴다. 잠금 대기를 직접 관측하는 테스트는 <c>Task.Delay</c>로 "요청이 잠금을 기다리는 중"인 순간을 만든다 — 10초 <c>lock_timeout</c> 자체를 기다리는 테스트는 없다(별도의 짧은 타임아웃으로 같은 메커니즘만 측정한다).</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class AttachmentIntegrityTests(PostgresContainerFixture pg)
{
    private static readonly byte[] Png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", "exif-text.png"));

    private static MultipartFormDataContent Form()
    {
        var part = new ByteArrayContent(Png);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { part, "file", "a.png" } };
    }

    private static async Task<AttachmentDto> UploadAsync(HttpClient client)
    {
        using var res = await client.PostAsync("/api/attachments", Form());
        Assert.Contains(res.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });
        return (await res.Content.ReadFromJsonAsync<AttachmentDto>(TestJson.Options))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, string key)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("k", key);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, string key)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("k", key);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>삭제와 같은 내용의 업로드는 그 내용의 잠금을 기다리고, 끝나면 잠금을 돌려준다. 잠금이 없는 구현에서는 두 요청이 즉시 끝나 실패한다.</summary>
    [Fact]
    public async Task DeleteAndUpload_OfTheSameContent_WaitForTheContentLock_AndReleaseIt()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var client = await factory.CreateLoggedInClientAsync();
        var uploaded = await UploadAsync(client);
        var key = AttachmentLock.KeyFor(uploaded.Sha256);

        await using var holder = new NpgsqlConnection(factory.ConnectionString);
        await holder.OpenAsync();
        await ExecuteAsync(holder, "SELECT pg_advisory_lock(hashtextextended(@k, 0))", key); // void를 돌려주므로 스칼라로 읽지 않는다

        var delete = client.DeleteAsync($"/api/attachments/{uploaded.Id}");
        var upload = client.PostAsync("/api/attachments", Form());
        // 800ms 마진: 느린 러너에서는 거짓 실패(진짜 잠겼는데 못 끝났다고 오판)가 아니라 "느린 러너에서는 잠금 없는 구현도 우연히 통과"하는
        // 쪽으로만 위험이 있다 — 이 머신에서는 DeleteAsync의 잠금을 걷어내는 사보타주로 이 값이 실제로 판별력을 갖는지 확인했다(실패 재현됨).
        var firstDone = await Task.WhenAny(delete, upload, Task.Delay(TimeSpan.FromMilliseconds(800)));
        Assert.True(firstDone != delete && firstDone != upload, "잠금을 쥐고 있는데 요청이 끝났다 — 잠금을 쓰지 않는다.");

        Assert.True(await ScalarAsync<bool>(holder, "SELECT pg_advisory_unlock(hashtextextended(@k, 0))", key));
        using var deleteRes = await delete;
        using var uploadRes = await upload;
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);
        Assert.Contains(uploadRes.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });

        // 두 요청이 잠금을 돌려줬다: 다른 세션이 바로 잡을 수 있다.
        Assert.True(await ScalarAsync<bool>(holder, "SELECT pg_try_advisory_lock(hashtextextended(@k, 0))", key));
        await AssertEveryRowHasItsFileAsync(factory);
    }

    /// <summary>삭제와 재업로드를 계속 교차시켜도 "파일 없는 행"이 생기지 않는다(불변식 검사 — 위 테스트가 메커니즘을, 이 테스트가 결과를 본다).
    /// 잠금이 두 요청을 직렬화하므로 라운드마다 "삭제가 먼저(행 0개로 끝남)" 또는 "업로드가 먼저(기존 행을 재조회, 행 1개로 끝남)" 둘 중 하나가 되는데,
    /// 이 테스트는 매 라운드 행이 있든 없든 무결성만 보고 끝나므로 모든 라운드가 항상 "행 0개"로만 끝나도(검사 루프가 매번 공집합을 돌아 아무것도 검증하지
    /// 않고도) 통과해 버릴 수 있다(계획과 다르게 한 것 참조) — 그래서 라운드별로 실제로 살펴본 행 수를 누적하고 마지막에 0보다 큰지 확인해,
    /// 이 불변식 검사가 실제로 최소 한 번은 "행이 있는" 경우를 검사했음을 보장한다.</summary>
    [Fact]
    public async Task InterleavedDeleteAndReupload_NeverLeavesARowWithoutItsFile()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var client = await factory.CreateLoggedInClientAsync();
        var rowsExamined = 0;
        for (var round = 0; round < 15; round++)
        {
            var current = await UploadAsync(client);
            var delete = client.DeleteAsync($"/api/attachments/{current.Id}");
            var upload = client.PostAsync("/api/attachments", Form());
            await Task.WhenAll(delete, upload);
            using var deleteRes = await delete;
            using var uploadRes = await upload;
            Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);
            Assert.Contains(uploadRes.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });
            rowsExamined += await AssertEveryRowHasItsFileAsync(factory);
        }
        // 검사 루프가 15라운드 내내 빈 목록만 돌아 아무 파일도 실제로 확인하지 않은 채 통과하는 거짓 양성을 막는다.
        Assert.True(rowsExamined > 0, "15라운드 내내 행이 하나도 없었다 — 무결성 검사가 실제로 아무것도 보지 못했다.");
    }

    /// <summary>업로드가 잠금을 기다리는 사이 같은 내용의 파일이 디스크에서 사라지면(다른 요청의 삭제·청소), 잠금을 잡은 뒤 파일 존재를 다시 확인해
    /// 없으면 <see cref="IFormFile"/>을 다시 열어 재저장한다. 결정적으로 만들기 위해 테스트가 먼저 그 sha의 잠금을 쥐고, 업로드를 시작한 뒤
    /// <see cref="Task.WhenAny(Task, Task)"/> + 유계 대기로 "업로드가 아직 안 끝났다(=잠금 대기 중)"를 확인하고 나서야 파일을 지운다 — 고정 지연만
    /// 쓰면, 두 번째 업로드 자신의 (잠금 밖) 최초 저장이 그 사이 끝나지 않은 채로 지워져 <c>FileSystemAttachmentStore.SaveAsync</c>의 통상적인
    /// "없으면 옮긴다" 동작만으로 통과해 버려 이 테스트가 실제로 검증하려는 "잠금 안 재확인·재오픈" 경로를 전혀 타지 않고도 통과할 수 있다.
    /// 이 테스트의 업로드는 작은(64KB 미만) 픽스처라 프레임워크가 메모리에 버퍼링한 <c>IFormFile</c>을 다시 여는 경로만 검증한다 — 64KB를 넘겨
    /// 디스크로 버퍼링되는 경로는 별도로 검증하지 않았다(미검증).</summary>
    [Fact]
    public async Task Upload_WhenFileVanishesWhileWaitingForTheLock_ReSavesItFromTheReopenedFormFile()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var client = await factory.CreateLoggedInClientAsync();
        var uploaded = await UploadAsync(client);
        var key = AttachmentLock.KeyFor(uploaded.Sha256);
        string physicalPath;
        await using (var scope = factory.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Attachments.AsNoTracking().SingleAsync(a => a.Id == uploaded.Id);
            physicalPath = scope.ServiceProvider.GetRequiredService<FileSystemAttachmentStore>().PhysicalPath(row.StoragePath);
        }

        await using var holder = new NpgsqlConnection(factory.ConnectionString);
        await holder.OpenAsync();
        await ExecuteAsync(holder, "SELECT pg_advisory_lock(hashtextextended(@k, 0))", key); // 테스트가 먼저 그 내용의 잠금을 쥔다.

        var upload = client.PostAsync("/api/attachments", Form());
        // 두 번째 업로드가 (잠금 밖) 최초 저장까지 끝내고 잠금 대기에 실제로 들어갔음을 확인한다 — 그렇지 않다면 요청이 800ms 안에 끝나 버렸을 것이다.
        var firstDone = await Task.WhenAny(upload, Task.Delay(TimeSpan.FromMilliseconds(800)));
        Assert.NotSame(upload, firstDone); // "잠금을 쥐고 있는데 요청이 끝났다"가 아니어야 한다 — 이때는 파일이 아직 살아 있으므로 지워도 안전하다.

        File.Delete(physicalPath);
        Assert.False(File.Exists(physicalPath), "사전 조건: 대기 중 파일을 지우지 못했다.");

        Assert.True(await ScalarAsync<bool>(holder, "SELECT pg_advisory_unlock(hashtextextended(@k, 0))", key));
        using var uploadRes = await upload;
        Assert.Contains(uploadRes.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });
        Assert.True(File.Exists(physicalPath), "잠금 안에서 파일을 다시 저장하지 못했다 — 기존 행만 재조회하고 실제 파일은 없는 상태로 끝났다.");
    }

    /// <summary>측정: <see cref="AttachmentLock.HoldAsync"/> 이후 <see cref="Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.SqlQueryRaw{TResult}"/>·
    /// LINQ 쿼리·<c>SaveChangesAsync</c>가 전부 같은 PostgreSQL 백엔드 세션(같은 <c>pg_backend_pid()</c>)에서 실행되는지, 그리고 <c>DisposeAsync</c>의
    /// 명시적 UNLOCK이 그 세션의 잠금을 실제로 풀어 다른 세션의 <c>pg_try_advisory_lock</c>을 성공시키는지 직접 관측한다(이 계획이 구현 전에 검증하지 못했던
    /// 핵심 불확실성 — HTTP를 거치는 <see cref="DeleteAndUpload_OfTheSameContent_WaitForTheContentLock_AndReleaseIt"/>은 이를 간접적으로만 증명한다).</summary>
    [Fact]
    public async Task HoldAsync_KeepsSubsequentEfCommandsOnTheSameSession_AndUnlockReleasesTheKey()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        _ = await factory.CreateLoggedInClientAsync(); // 호스트를 기동해 Migrate()를 끝낸다(아래에서 스코프를 직접 쓰기 전에 필요).
        var sha = new string('7', 64);
        var key = AttachmentLock.KeyFor(sha);

        await using var checker = new NpgsqlConnection(factory.ConnectionString);
        await checker.OpenAsync();

        await using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using (await AttachmentLock.HoldAsync(db, sha, CancellationToken.None))
            {
                var pidBefore = (await db.Database.SqlQueryRaw<int>("SELECT pg_backend_pid()").ToListAsync())[0];
                _ = await db.Attachments.CountAsync(); // LINQ 쿼리도 같은 연결에서 실행되는지 확인하는 경로
                // 프로브 행: CK_Attachments_Sha256이 소문자 hex 64자를 요구하므로 Guid 2개를 이어붙여 64자 hex 문자열을 만든다.
                var probeSha = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")).ToLowerInvariant();
                var probe = new Attachment
                {
                    FileName = "probe.png", ContentType = "image/png", SizeBytes = 1,
                    StoragePath = "zz/probe-" + sha[..8] + ".png", Sha256 = probeSha, CreatedAt = DbClock.UtcNow(),
                };
                db.Attachments.Add(probe);
                await db.SaveChangesAsync(); // SaveChangesAsync도 같은 연결에서 실행되는지 확인하는 경로
                var pidAfter = (await db.Database.SqlQueryRaw<int>("SELECT pg_backend_pid()").ToListAsync())[0];
                Assert.Equal(pidBefore, pidAfter); // 잠금을 잡은 세션과 그 뒤 모든 EF 명령이 실행된 세션이 같다(측정)

                // 잠금을 쥔 채로는 다른 세션이 못 잡는다.
                Assert.False(await ScalarAsync<bool>(checker, "SELECT pg_try_advisory_lock(hashtextextended(@k, 0))", key));

                await db.Attachments.Where(a => a.Id == probe.Id).ExecuteDeleteAsync(); // 프로브 행 정리
            }
        }

        // DisposeAsync의 명시적 UNLOCK이 실제로 키를 풀어, 다른 세션이 바로 잡을 수 있다(측정).
        Assert.True(await ScalarAsync<bool>(checker, "SELECT pg_try_advisory_lock(hashtextextended(@k, 0))", key));
        Assert.True(await ScalarAsync<bool>(checker, "SELECT pg_advisory_unlock(hashtextextended(@k, 0))", key));
    }

    /// <summary>측정: 세션 advisory lock(<c>pg_advisory_lock</c>)을 잡은 세션이 있으면, 다른 세션이 짧은 <c>lock_timeout</c> 아래에서 같은 잠금을
    /// 시도할 때 대기가 그 시간 안에 SqlState 55P03("canceling statement due to lock timeout")으로 끊긴다. 스파이크 S7은 트랜잭션 잠금
    /// (<c>pg_advisory_xact_lock</c>)만 측정했으므로, <see cref="AttachmentLock.HoldAsync"/>가 실제로 쓰는 세션 잠금에 대해 같은 성질을
    /// 별도로 측정한다(10초 상한 자체는 기다리지 않고 300ms로 같은 메커니즘만 확인한다).</summary>
    [Fact]
    public async Task SessionAdvisoryLock_WithShortLockTimeout_FailsWith55P03_WhileHeldByAnotherSession()
    {
        var key = "measure:" + Guid.NewGuid().ToString("N");
        await using var holder = new NpgsqlConnection(pg.ConnectionString);
        await holder.OpenAsync();
        await ExecuteAsync(holder, "SELECT pg_advisory_lock(hashtextextended(@k, 0))", key);

        await using var waiter = new NpgsqlConnection(pg.ConnectionString);
        await waiter.OpenAsync();
        await ExecuteAsync(waiter, "SET lock_timeout = '300ms'", key);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(waiter, "SELECT pg_advisory_lock(hashtextextended(@k, 0))", key));
        sw.Stop();
        Assert.Equal("55P03", ex.SqlState);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"lock_timeout이 300ms인데 {sw.Elapsed}가 걸렸다.");

        Assert.True(await ScalarAsync<bool>(holder, "SELECT pg_advisory_unlock(hashtextextended(@k, 0))", key));
    }

    /// <summary>측정: <c>SET lock_timeout</c>은 세션(연결) 범위다 — Npgsql 풀에서 같은 물리 연결을 재사용해도(Task 4가 실측한, 풀 반환 시
    /// 세션이 리셋되는 것과 같은 메커니즘) 이전 세션이 남긴 <c>lock_timeout</c> 값이 다음 사용자에게 새지 않는다. <c>Maximum Pool Size=1</c>로
    /// 같은 물리 연결(같은 <c>pg_backend_pid()</c>)이 재사용됨을 먼저 확인한 뒤 값을 비교한다.</summary>
    [Fact]
    public async Task SetLockTimeout_DoesNotLeakToTheNextUserOfThePooledConnection()
    {
        var csb = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            MaxPoolSize = 1, MinPoolSize = 0, ApplicationName = "attachment-lock-timeout-leak-" + Guid.NewGuid().ToString("N"),
        };
        var cs = csb.ToString();
        try
        {
            int pidA;
            await using (var a = new NpgsqlConnection(cs))
            {
                await a.OpenAsync();
                pidA = (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", a).ExecuteScalarAsync())!;
                await new NpgsqlCommand("SET lock_timeout = '250ms'", a).ExecuteNonQueryAsync();
            } // Close(): 풀로 반환(Maximum Pool Size=1이라 물리 연결은 살아 있다).

            await using var b = new NpgsqlConnection(cs);
            await b.OpenAsync();
            var pidB = (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", b).ExecuteScalarAsync())!;
            Assert.Equal(pidA, pidB); // 사전 조건: 같은 물리 연결을 재사용했다(그렇지 않으면 이 측정이 의미가 없다)

            var lockTimeoutAfterReuse = (string)(await new NpgsqlCommand("SHOW lock_timeout", b).ExecuteScalarAsync())!;
            Assert.NotEqual("250ms", lockTimeoutAfterReuse);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(cs);
            NpgsqlConnection.ClearPool(cleanup);
        }
    }

    /// <summary>측정(Fix round 1 — 이전 보고서의 결론 정정): 세션 advisory lock을 잡은 뒤 명시적으로 <c>pg_advisory_unlock</c>을 부르지 않고
    /// 연결을 정상적으로 닫으면(Npgsql 풀로 반환, 물리 연결·백엔드 세션은 살아 있음) 잠금은 <b>반환 직후에는</b> 풀리지 않는다 — 여기까지는 이전 측정과
    /// 같다. 하지만 "풀 반환의 리셋이 advisory lock까지는 미치지 않을 만큼 좁다"는 이전 결론은 틀렸다: 실제로는 리셋 자체가 <b>지연</b>된다 — Npgsql은
    /// 반환 시 세션 리셋 SQL을 그 물리 연결의 쓰기 버퍼에 prepend만 해 두고, <b>다음 대여자가 그 연결로 보내는 첫 명령과 함께</b> 실제로 전송한다.
    /// 그래서 같은 물리 연결(같은 <c>pg_backend_pid()</c>)을 다시 빌려 아무 명령이나 실행하면 — 그 시점에 리셋이 실제로 실행되어 — 그 세션이 쥔
    /// advisory lock 수가 0이 되고 다른 세션의 <c>pg_try_advisory_lock</c>도 성공한다. 이 한 가지 메커니즘(지연된 리셋)이 이 테스트의 결과와
    /// <see cref="SetLockTimeout_DoesNotLeakToTheNextUserOfThePooledConnection"/>(같은 물리 연결 재사용 시 <c>lock_timeout</c>이 기본값으로 돌아와 있음)을
    /// 모두 설명한다 — Npgsql이 실제로 보내는 문장이 PostgreSQL의 <c>DISCARD ALL</c>(GUC 리셋 + <c>pg_advisory_unlock_all()</c> 포함)인지는 Npgsql
    /// 내부까지 추적하지 않아 확인하지 못했다(관측 사실만 적는다: <c>lock_timeout</c> 복귀 + advisory lock 해제, 둘 다 재사용 시점에 함께 관측됨).</summary>
    [Fact]
    public async Task ClosingAPooledConnection_WithoutAnExplicitUnlock_DelaysReleaseUntilThePhysicalConnectionIsNextUsed()
    {
        var key = "measure-noreset:" + Guid.NewGuid().ToString("N");
        var csb = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            MaxPoolSize = 1, MinPoolSize = 0, ApplicationName = "attachment-lock-no-unlock-" + Guid.NewGuid().ToString("N"),
        };
        var cs = csb.ToString();
        try
        {
            int pidA;
            await using (var a = new NpgsqlConnection(cs))
            {
                await a.OpenAsync();
                pidA = (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", a).ExecuteScalarAsync())!;
                await ExecuteAsync(a, "SELECT pg_advisory_lock(hashtextextended(@k, 0))", key);
                // UNLOCK을 부르지 않고 그대로 닫는다: Releaser.DisposeAsync에서 UNLOCK 실행 자체가 실패한 뒤 CloseConnectionAsync만 도는 경로를 흉내낸다.
            } // Close(): Maximum Pool Size=1이라 물리 연결·백엔드 세션은 살아 있는 채로 풀에 반환된다(폐기되지 않는다).

            await using var checker = new NpgsqlConnection(pg.ConnectionString);
            await checker.OpenAsync();
            // 반환 직후: 아직 안 풀렸다(이전 측정과 같음 — "닫으면 바로 풀린다"가 아니다). 실측(원시값): False.
            Assert.False(await ScalarAsync<bool>(checker, "SELECT pg_try_advisory_lock(hashtextextended(@k, 0))", key));

            // 같은 물리 연결을 다시 빌려 명령을 실행한다 — Npgsql이 반환 시 예약해 둔 세션 리셋이 이 시점에 실제로 전송된다.
            await using var b = new NpgsqlConnection(cs);
            await b.OpenAsync();
            var pidB = (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", b).ExecuteScalarAsync())!;
            Assert.Equal(pidA, pidB); // 사전 조건: 같은 물리 연결을 재사용했다(그렇지 않으면 이 측정이 의미가 없다)

            // 재사용된 세션 스스로가 쥔 advisory lock 수 — 리셋이 실제로 이 세션의 advisory lock을 풀었는지 pg_locks로 직접 확인한다. 실측(원시값): 0.
            var heldByReusedSession = (long)(await new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid()", b).ExecuteScalarAsync())!;
            Assert.Equal(0, heldByReusedSession);

            // 다른 세션도 이제(재사용 시점 이후) 잡을 수 있다 — pg_try_advisory_lock은 성공하면 그 자리에서 새로 잠금을 잡으므로 아래에서 되돌려준다. 실측(원시값): True.
            Assert.True(await ScalarAsync<bool>(checker, "SELECT pg_try_advisory_lock(hashtextextended(@k, 0))", key));
            Assert.True(await ScalarAsync<bool>(checker, "SELECT pg_advisory_unlock(hashtextextended(@k, 0))", key));
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(cs);
            NpgsqlConnection.ClearPool(cleanup);
        }
    }

    // 반환값(살펴본 행 수)은 호출부가 "검사 루프가 실제로 무언가를 봤는지"를 누적해 판별할 수 있게 한다(위 InterleavedDeleteAndReupload의 공집합 통과 방지).
    private static async Task<int> AssertEveryRowHasItsFileAsync(ApiFactory factory)
    {
        await using var scope = factory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<FileSystemAttachmentStore>();
        var paths = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Attachments.AsNoTracking().Select(a => a.StoragePath).ToListAsync();
        foreach (var path in paths)
        {
            Assert.True(File.Exists(store.PhysicalPath(path)), $"파일 없는 행: {path}");
        }
        return paths.Count;
    }
}
