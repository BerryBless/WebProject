using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Storage;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>같은 내용의 삭제·업로드가 하나의 잠금(<c>GET_LOCK</c>)으로 직렬화되는지 검증한다. 테스트가 그 잠금을 직접 쥐고 요청이 기다리는 것을 본다
/// (경쟁을 확률에 맡기지 않는다). 잠금 메커니즘 자체(같은 세션 고정·명시적 해제·타임아웃·DB별 격리·풀 재대여 해제)는
/// <see cref="HoldAsync_KeepsEfCommandsOnTheLockingSession_AndReleaseFreesTheName"/>·<see cref="HoldAsync_WhenHeldElsewhere_TimesOutAsLockTimeout"/>·
/// <see cref="SameSha_InAnotherDatabase_DoesNotContend"/>·<see cref="UnreleasedLock_IsFreedWhenThePooledConnectionIsReused"/>가 측정한다(전환 계획 Task 6).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 시작하고, <see cref="HttpClient"/> 요청·<see cref="MySqlConnection"/> 왕복은 각각 자신의 비동기 흐름으로 진행된다. 여러 테스트가 같은 <see cref="MySqlContainerFixture"/> 컨테이너를 공유하고, <see cref="DeleteAndUpload_OfTheSameContent_WaitForTheContentLock_AndReleaseIt"/>·<see cref="InterleavedDeleteAndReupload_NeverLeavesARowWithoutItsFile"/>·<see cref="Upload_WhenFileVanishesWhileWaitingForTheLock_ReSavesItFromTheReopenedFormFile"/> 세 테스트는 같은 픽스처(<see cref="Png"/>)를 올리므로 내용 SHA가 <b>같다</b> — 그래도 서로 간섭하지 않는 이유는 테스트마다 격리된 <see cref="ApiFactory"/>가 각자 별도의 DB를 쓰고, 서버 전역인 <c>GET_LOCK</c> 이름에 DB 이름 해시가 들어가기 때문이다(<see cref="AttachmentLock.NameFor"/>). <see cref="SameSha_InAnotherDatabase_DoesNotContend"/>는 이를 두 개의 <see cref="ApiFactory"/>(= 두 DB)로 직접 증명한다.</description></item>
/// <item><description><b>Memory Policy:</b> 테스트마다 격리된 <see cref="ApiFactory"/>(자체 DB + 자체 임시 첨부 폴더)를 쓴다.</description></item>
/// <item><description><b>Blocking:</b> 비동기. 실제 MySQL 컨테이너를 쓴다. 잠금 대기를 직접 관측하는 테스트는 <c>Task.Delay</c>나 대기자 관측(<c>performance_schema.metadata_locks</c>)으로 "요청이 잠금을 기다리는 중"인 순간을 만든다 — 10초 대기 상한 자체를 기다리는 테스트는 없다. 테스트가 직접 여는 연결은 <c>Pooling=false</c>라 해제 즉시 서버 세션이 닫힌다(쥐고 있던 잠금이 풀에 남지 않는다).</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class AttachmentIntegrityTests(MySqlContainerFixture mysql)
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

    // 스칼라 하나를 long?로 읽는다. MySqlConnector는 GET_LOCK·RELEASE_LOCK·COUNT 결과를 정수(long)로, 실패 시 NULL(DBNull)로 준다 — bool 캐스트는 InvalidCastException.
    private static async Task<long?> ScalarAsync(MySqlConnection connection, string sql, string name)
    {
        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@k", name);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    // 테스트가 직접 여는 연결은 풀을 끈다: 팩토리 Dispose는 앱이 실제로 쓰는 EF 연결 풀(AppDbContext·PublicDbContext)만 비우므로,
    // 테스트가 직접 만든 별도 풀링 연결은 잠금·소켓을 쥔 채 남을 수 있다.
    private static async Task<MySqlConnection> OpenUnpooledAsync(ApiFactory factory)
    {
        var connection = new MySqlConnection(new MySqlConnectionStringBuilder(factory.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>삭제와 같은 내용의 업로드는 그 내용의 잠금을 기다리고, 끝나면 잠금을 돌려준다. 잠금이 없는 구현에서는 두 요청이 즉시 끝나 실패한다.</summary>
    [Fact]
    public async Task DeleteAndUpload_OfTheSameContent_WaitForTheContentLock_AndReleaseIt()
    {
        using var factory = new ApiFactory(mysql, new Dictionary<string, string?>());
        using var client = await factory.CreateLoggedInClientAsync();
        var uploaded = await UploadAsync(client);
        var name = AttachmentLock.NameFor(factory.DatabaseName, uploaded.Sha256);

        await using var holder = await OpenUnpooledAsync(factory);
        Assert.Equal(1L, await ScalarAsync(holder, "SELECT GET_LOCK(@k, 0)", name)); // 아무도 쥐지 않았으니 즉시 1

        var delete = client.DeleteAsync($"/api/attachments/{uploaded.Id}");
        var upload = client.PostAsync("/api/attachments", Form());
        // 800ms 마진: 느린 러너에서는 거짓 실패(진짜 잠겼는데 못 끝났다고 오판)가 아니라 "느린 러너에서는 잠금 없는 구현도 우연히 통과"하는
        // 쪽으로만 위험이 있다 — 이 머신에서는 DeleteAsync의 잠금을 걷어내는 사보타주로 이 값이 실제로 판별력을 갖는지 확인했다(실패 재현됨).
        var firstDone = await Task.WhenAny(delete, upload, Task.Delay(TimeSpan.FromMilliseconds(800)));
        Assert.True(firstDone != delete && firstDone != upload, "잠금을 쥐고 있는데 요청이 끝났다 — 잠금을 쓰지 않는다.");

        Assert.Equal(1L, await ScalarAsync(holder, "SELECT RELEASE_LOCK(@k)", name));
        using var deleteRes = await delete;
        using var uploadRes = await upload;
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);
        Assert.Contains(uploadRes.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });

        // 두 요청이 잠금을 돌려줬다: 다른 세션이 바로 잡을 수 있다.
        Assert.Equal(1L, await ScalarAsync(holder, "SELECT GET_LOCK(@k, 0)", name));
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
        using var factory = new ApiFactory(mysql, new Dictionary<string, string?>());
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
    /// 없으면 <see cref="IFormFile"/>을 다시 열어 재저장한다. 테스트가 먼저 그 sha의 잠금을 쥐고 업로드를 시작한 뒤, <b>이 데이터베이스에서 아직
    /// 허가되지 않은 사용자 잠금 대기자 행(<c>performance_schema.metadata_locks</c>의 PENDING)을 실제로 관측한 뒤에만</b> 파일을 지운다 — 즉 업로드가 (잠금 밖) 최초 저장을 이미
    /// 끝내고 잠금 대기에 들어갔음을 확인한 다음에 지운다. 고정 지연만 쓰면 느린 러너에서 그 최초 저장이 끝나기 전에 파일을 지워
    /// <c>FileSystemAttachmentStore.SaveAsync</c>의 통상적인 "없으면 옮긴다" 동작만으로 통과해 버려, 이 테스트가 검증하려는 "잠금 안 재확인·재오픈"
    /// 경로를 전혀 타지 않고도 통과할 수 있다.
    /// 대기자 조회는 잠금 이름(<see cref="AttachmentLock.NameFor"/>, DB 이름 해시 포함)으로 이 테스트 전용 DB에 한정한다 —
    /// <c>metadata_locks</c>는 서버 전역이라 이름 조건이 없으면 같은 컨테이너의 다른 테스트가 만든 대기를 자기 것으로 오인할 수 있다.
    /// 그 조건 덕분에 "컬렉션이 직렬 실행되므로 안전하다" 같은 외부 전제 없이 이 테스트 안에서 판별이 닫힌다.
    /// 이 테스트의 업로드는 작은(64KB 미만) 픽스처라 프레임워크가 메모리에 버퍼링한 <c>IFormFile</c>을 다시 여는 경로만 검증한다 — 64KB를 넘겨
    /// 디스크로 버퍼링되는 경로는 별도로 검증하지 않았다(미검증).</summary>
    [Fact]
    public async Task Upload_WhenFileVanishesWhileWaitingForTheLock_ReSavesItFromTheReopenedFormFile()
    {
        using var factory = new ApiFactory(mysql, new Dictionary<string, string?>());
        using var client = await factory.CreateLoggedInClientAsync();
        var uploaded = await UploadAsync(client);
        var name = AttachmentLock.NameFor(factory.DatabaseName, uploaded.Sha256);
        string physicalPath;
        await using (var scope = factory.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Attachments.AsNoTracking().SingleAsync(a => a.Id == uploaded.Id);
            physicalPath = scope.ServiceProvider.GetRequiredService<FileSystemAttachmentStore>().PhysicalPath(row.StoragePath);
        }

        await using var holder = await OpenUnpooledAsync(factory);
        Assert.Equal(1L, await ScalarAsync(holder, "SELECT GET_LOCK(@k, 0)", name)); // 테스트가 먼저 그 내용의 잠금을 쥔다.

        var upload = client.PostAsync("/api/attachments", Form());
        // holder는 이 순간에도 잠금을 쥔 세션이므로 폴링에 쓰지 않는다(같은 연결로 명령을 겹쳐 보내지 않는다) — 별도 연결을 연다.
        await using var watcher = await OpenUnpooledAsync(factory);
        await WaitForBlockedLockWaiterAsync(watcher, name);

        File.Delete(physicalPath);
        Assert.False(File.Exists(physicalPath), "사전 조건: 대기 중 파일을 지우지 못했다.");

        Assert.Equal(1L, await ScalarAsync(holder, "SELECT RELEASE_LOCK(@k)", name));
        using var uploadRes = await upload;
        Assert.Contains(uploadRes.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });
        Assert.True(File.Exists(physicalPath), "잠금 안에서 파일을 다시 저장하지 못했다 — 기존 행만 재조회하고 실제 파일은 없는 상태로 끝났다.");
    }

    // 이 잠금 이름의 "허가되지 않은(PENDING) 사용자 잠금" 행이 하나라도 보일 때까지 유계 폴링한다. metadata_locks는 서버 전역이지만
    // 잠금 이름에 DB 이름 해시가 들어가므로 이름 조건만으로 이 테스트 전용 DB에 한정된다.
    // 10초 상한은 "대기자를 못 봤다"를 매달림이 아니라 명시적 실패로 만들기 위한 것이다(업로드의 잠금 대기 상한 자체는 AttachmentLock.WaitSeconds로 별개다).
    private static async Task WaitForBlockedLockWaiterAsync(MySqlConnection connection, string name)
    {
        const string sql = """
            SELECT COUNT(*) FROM performance_schema.metadata_locks
            WHERE OBJECT_TYPE = 'USER LEVEL LOCK' AND OBJECT_NAME = @k AND LOCK_STATUS = 'PENDING'
            """;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await ScalarAsync(connection, sql, name) >= 1) return;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        Assert.Fail("10초 안에 사용자 잠금 대기자 행을 보지 못했다 — 업로드가 잠금 대기에 들어갔음을 확인하지 못한 채로는 파일을 지울 수 없다.");
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

    // 아래 4개 테스트는 AttachmentLock 자체(GET_LOCK 세션 고정·타임아웃·DB별 격리·풀 재대여 해제)를 MySQL 기준으로 측정한다(전환 계획 Task 6).
    // 실제 콘텐츠 SHA와 무관한 고정값이라 임의의 64자 hex를 그대로 쓴다.
    private const string LockSha = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    // 이 클래스 위쪽의 3-인자 ScalarAsync(파라미터화된 @k)를 재사용할 수 없는 경우(잠금 이름 없이 CONNECTION_ID()만 읽을 때) 전용.
    // UnreleasedLock_IsFreedWhenThePooledConnectionIsReused가 의도적으로 여는 "풀링된" 원시 연결에서만 쓴다.
    private static async Task<long> ScalarAsync(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>잠금을 쥔 동안 같은 컨텍스트의 EF 명령은 잠근 세션(CONNECTION_ID)을 쓰고, 해제하면 다른 세션이 즉시 잡을 수 있다.</summary>
    [Fact]
    public async Task HoldAsync_KeepsEfCommandsOnTheLockingSession_AndReleaseFreesTheName()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var name = AttachmentLock.NameFor(factory.DatabaseName, LockSha);
        await using var observer = await OpenUnpooledAsync(factory);

        await using (await AttachmentLock.HoldAsync(db, LockSha, CancellationToken.None))
        {
            // EF 쪽을 먼저 읽는다: HoldAsync가 연결을 명시적으로 열어 두지 않으면(변이 확인 대상) EF가 이 명령을 위해
            // 풀에서 새로 연결을 빌렸다가 돌려주면서 ConnectionReset이 잠금을 풀어 버린다 — observer를 먼저 읽으면 그 순간에는
            // 아직 잠금이 살아 있어(반납 시점 해제는 보장 아님, 스파이크 S6b) 우연히 같은 값이 나올 수 있어 판별력이 없다.
            var efSession = await db.Database.SqlQueryRaw<long>("SELECT CAST(CONNECTION_ID() AS SIGNED) AS `Value`").SingleAsync();
            var holder = await ScalarAsync(observer, "SELECT IS_USED_LOCK(@k)", name);
            Assert.Equal((long?)efSession, holder);
        }
        Assert.Equal(1L, await ScalarAsync(observer, "SELECT IS_FREE_LOCK(@k)", name));
    }

    /// <summary>다른 세션이 쥐고 있으면 대기 상한 뒤 DbLockTimeoutException(→ 503)이 나고, 실패 경로에서 연결을 닫는다.</summary>
    [Fact]
    public async Task HoldAsync_WhenHeldElsewhere_TimesOutAsLockTimeout()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        var name = AttachmentLock.NameFor(factory.DatabaseName, LockSha);
        await using var other = await OpenUnpooledAsync(factory);
        Assert.Equal(1L, await ScalarAsync(other, "SELECT GET_LOCK(@k, 0)", name));

        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ex = await Assert.ThrowsAsync<DbLockTimeoutException>(() => AttachmentLock.HoldAsync(db, LockSha, waitSeconds: 1, CancellationToken.None));
        Assert.Equal(DbErrorKind.LockTimeout, DbErrorClassifier.Classify(ex));
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    /// <summary>잠금 이름은 DB별이다: 다른 DB(다른 팩토리)의 같은 SHA 잠금과 서로 기다리지 않는다(R3).</summary>
    [Fact]
    public async Task SameSha_InAnotherDatabase_DoesNotContend()
    {
        using var first = new ApiFactory(mysql);
        using var second = new ApiFactory(mysql);
        using var c1 = first.CreateClient();
        using var c2 = second.CreateClient();
        await using var s1 = first.CreateScope();
        await using var s2 = second.CreateScope();
        await using (await AttachmentLock.HoldAsync(s1.ServiceProvider.GetRequiredService<AppDbContext>(), LockSha, CancellationToken.None))
        await using (await AttachmentLock.HoldAsync(s2.ServiceProvider.GetRequiredService<AppDbContext>(), LockSha, waitSeconds: 1, CancellationToken.None))
        {
            // 두 번째 획득이 1초 대기 없이 성공해야 여기에 도달한다
        }
    }

    /// <summary>
    /// 해제하지 않고 풀에 반납된 잠금은 같은 물리 연결이 다시 대여될 때 ConnectionReset으로 풀린다(Releaser의 해제 실패 경로가 기대는 성질, 스파이크 S6b).
    /// 반납 직후에는 아직 쥐어져 있을 수 있으므로(스파이크 S6b에서 반납 직후 관측값은 0) 재대여 뒤를 단언한다.
    /// </summary>
    [Fact]
    public async Task UnreleasedLock_IsFreedWhenThePooledConnectionIsReused()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        var name = AttachmentLock.NameFor(factory.DatabaseName, LockSha);
        var single = new MySqlConnectionStringBuilder(DataServiceCollectionExtensions.WithSessionReset(factory.ConnectionString)) { MaximumPoolSize = 1 }.ConnectionString;
        try
        {
            long id;
            await using (var c = new MySqlConnection(single))
            {
                await c.OpenAsync();
                id = await ScalarAsync(c, "SELECT CONNECTION_ID()"); // 잠금 이름과 무관한 스칼라라 파라미터화된 3-인자 오버로드를 쓰지 않는다.
                Assert.Equal(1L, await ScalarAsync(c, "SELECT GET_LOCK(@k, 0)", name));
            }
            await using (var c = new MySqlConnection(single))
            {
                await c.OpenAsync();
                Assert.Equal(id, await ScalarAsync(c, "SELECT CONNECTION_ID()"));
                Assert.Equal(1L, await ScalarAsync(c, "SELECT IS_FREE_LOCK(@k)", name));
            }
        }
        finally
        {
            using var clear = new MySqlConnection(single);
            MySqlConnection.ClearPool(clear);
        }
    }
}
