using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>청소 잡은 "오래됐고, 내용 주소 모양이고, 참조하는 행이 없는" 파일만 지운다. 백그라운드 실행은 끄고 <c>SweepOnceAsync</c>를 직접 부른다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 호출된다. <c>SweepOnceAsync</c> 내부의 파일 열거·DB 조회는 이 테스트가 직접 <c>await</c>한다.</description></item>
/// <item><description><b>Memory Policy:</b> 테스트마다 격리된 <see cref="ApiFactory"/>(자체 DB + 자체 임시 첨부 폴더)를 쓴다. 다른 테스트와 공유하는 파일·행이 없다.</description></item>
/// <item><description><b>Concurrency:</b> 이 클래스 안의 테스트는 각자 자기 <see cref="ApiFactory"/>를 만들어 병렬로 실행돼도 서로 간섭하지 않는다. <see cref="AttachmentOptions.JanitorEnabled"/>가 테스트 기본값에서 꺼져 있어(<see cref="ApiFactory.ConfigureWebHost"/>) 백그라운드 <see cref="AttachmentJanitor"/> 루프가 파일 시각을 조작하는 이 테스트와 경합하지 않는다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class AttachmentJanitorTests(PostgresContainerFixture pg)
{
    private static async Task<AttachmentDto> UploadAsync(HttpClient client, string fixture)
    {
        var part = new ByteArrayContent(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", fixture)));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var res = await client.PostAsync("/api/attachments", new MultipartFormDataContent { { part, "file", fixture } });
        return (await res.Content.ReadFromJsonAsync<AttachmentDto>(TestJson.Options))!;
    }

    /// <summary>참조되는 오래된 파일은 남기고, 행은 있는데 파일이 없는 것은 보고만 하고, 참조 없는 오래된 파일과 오래된 임시 파일만 지우며,
    /// 새것(진행 중일 수 있는 것)과 모양이 규칙 밖인 것은 오래됐어도 건드리지 않는다.</summary>
    [Fact]
    public async Task Sweep_DeletesOnlyOldUnreferencedContentFiles_AndReportsMissingOnes()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var client = await factory.CreateLoggedInClientAsync();
        var store = factory.Services.GetRequiredService<FileSystemAttachmentStore>();
        var old = DateTime.UtcNow - AttachmentJanitor.MinimumAge - TimeSpan.FromMinutes(5);
        var root = factory.AttachmentsRoot;

        // 1) 참조되는 오래된 파일 → 남는다
        var kept = await UploadAsync(client, "exif-text.png");
        var keptPath = Directory.EnumerateFiles(root, kept.Sha256 + ".*", SearchOption.AllDirectories).Single();
        File.SetLastWriteTimeUtc(keptPath, old);
        // 2) 행은 있는데 파일이 없다 → 보고만 한다
        var broken = await UploadAsync(client, "exif-gps.jpg");
        File.Delete(Directory.EnumerateFiles(root, broken.Sha256 + ".*", SearchOption.AllDirectories).Single());
        // 3) 참조 없는 파일: 오래된 것은 지우고 새것은 둔다(진행 중인 업로드일 수 있다)
        string Orphan(char fill, DateTime? stamp)
        {
            var sha = new string(fill, 64);
            var path = Path.Combine(root, sha[..2], sha + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1, 2, 3]);
            if (stamp is { } at) File.SetLastWriteTimeUtc(path, at);
            return path;
        }
        var oldOrphan = Orphan('a', old);
        var freshOrphan = Orphan('b', null);
        // 4) 임시 파일: 오래된 것만
        Directory.CreateDirectory(Path.Combine(root, ".tmp"));
        var oldTemp = Path.Combine(root, ".tmp", "dead.upload");
        var freshTemp = Path.Combine(root, ".tmp", "live.upload");
        File.WriteAllBytes(oldTemp, [1]);
        File.WriteAllBytes(freshTemp, [1]);
        File.SetLastWriteTimeUtc(oldTemp, old);
        // 5) 모양이 규칙 밖인 오래된 파일 → 건드리지 않는다
        var foreign = Path.Combine(root, "aa", "README.txt");
        File.WriteAllText(foreign, "사람이 둔 파일");
        File.SetLastWriteTimeUtc(foreign, old);

        var result = await factory.Services.GetRequiredService<AttachmentJanitor>().SweepOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(new SweepResult(TempFilesDeleted: 1, OrphanFilesDeleted: 1, RowsMissingFiles: 1), result);
        Assert.True(File.Exists(keptPath));
        Assert.False(File.Exists(oldOrphan));
        Assert.True(File.Exists(freshOrphan));
        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(freshTemp));
        Assert.True(File.Exists(foreign));
        Assert.True(store.Exists(Path.GetRelativePath(root, keptPath).Replace('\\', '/'))); // Exists 접근자가 실제 경로 규칙과 맞는다
    }
}
