using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Storage;
using Xunit.Abstractions;

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
public sealed class AttachmentJanitorTests(PostgresContainerFixture pg, ITestOutputHelper output)
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

    /// <summary>버킷 디렉터리가 디렉터리 링크(저장 루트 밖 실제 디렉터리를 가리킴)면, 그 안에 오래되고 모양이 맞는 파일이 있어도 스윕이 링크를
    /// 따라가지 않아 지워지지 않는다. Windows에서 심볼릭 링크 생성은 권한(관리자·개발자 모드)을 요구할 수 있으므로 그때는 정션(<c>mklink /J</c>)으로
    /// 대체한다 — 정션도 reparse point라 <see cref="DirectoryInfo.LinkTarget"/>이 non-null이고, 스윕이 검사하는 성질이 심볼릭 링크와 같다.
    /// 둘 다 실패할 때만 사유를 출력하고 건너뛴다(그래야 이 방어가 검증 없이 통과하는 환경이 줄어든다).</summary>
    [Fact]
    public async Task Sweep_DoesNotFollowASymbolicLinkBucket_ToDeleteFilesOutsideTheRoot()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        var root = factory.AttachmentsRoot;
        Directory.CreateDirectory(root);

        // 링크가 가리킬, 저장 루트 밖의 진짜 디렉터리. 그 안에 "오래되고 모양이 맞는" 파일을 둔다.
        var outsideTarget = Path.Combine(Path.GetTempPath(), "portfolioblog-tests-link-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideTarget);
        try
        {
            var sha = new string('c', 64);
            var outsideFile = Path.Combine(outsideTarget, sha + ".png");
            File.WriteAllBytes(outsideFile, [1, 2, 3]);
            var old = DateTime.UtcNow - AttachmentJanitor.MinimumAge - TimeSpan.FromMinutes(5);
            File.SetLastWriteTimeUtc(outsideFile, old);

            var linkedBucket = Path.Combine(root, sha[..2]);
            if (!await TryCreateDirectoryLinkAsync(linkedBucket, outsideTarget)) return;
            Assert.NotNull(new DirectoryInfo(linkedBucket).LinkTarget); // 사전 조건: 스윕이 검사하는 성질(reparse point)을 실제로 만들었다

            var janitor = factory.Services.GetRequiredService<AttachmentJanitor>();
            await janitor.SweepOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.True(File.Exists(outsideFile), "링크를 따라가 링크 밖(진짜) 파일을 지웠다.");
        }
        finally
        {
            // 링크(reparse point) 자체만 제거한다: 비재귀 Directory.Delete는 링크 항목만 지우고 링크가 가리키는 대상 디렉터리의
            // 내용에는 손대지 않는다. 재귀 삭제를 쓰면 대상 디렉터리 안(저장 루트 밖!)의 파일까지 지울 위험을 문서화된 동작에만 의존하게 된다.
            // factory의 using 처분(AttachmentsRoot 재귀 삭제)보다 먼저 여기서 링크를 치워, 그 처분이 링크를 다루는 방식에도 기대지 않는다.
            var linkedBucket = Path.Combine(root, new string('c', 2));
            if (Directory.Exists(linkedBucket)) Directory.Delete(linkedBucket);
            if (Directory.Exists(outsideTarget)) Directory.Delete(outsideTarget, recursive: true);
        }
    }

    // 심볼릭 링크 → (Windows면) 정션 순으로 시도한다. Windows의 심볼릭 링크 생성은 SeCreateSymbolicLinkPrivilege(관리자 또는 개발자 모드)를
    // 요구하지만 정션(디렉터리 마운트 지점)은 요구하지 않아 일반 권한으로 만들 수 있고, .NET은 정션도 reparse point로 보아
    // DirectoryInfo.LinkTarget을 non-null로 돌려준다 — 스윕의 링크 검사가 보는 성질이 심볼릭 링크와 같다(실측: 이 개발 PC에서 mklink /J 성공, LinkTarget non-null).
    // mklink는 cmd.exe 내장 명령이라 별도 실행 파일이 없어 cmd.exe를 거쳐야 하고, 인자는 cmd가 다시 파싱하므로 ArgumentList로 쪼개지 않고 한 문자열로 넘긴다.
    private async Task<bool> TryCreateDirectoryLinkAsync(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (!OperatingSystem.IsWindows())
            {
                output.WriteLine($"디렉터리 링크를 만들 수 없어 이 테스트를 건너뛴다({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
            output.WriteLine($"심볼릭 링크 생성 실패({ex.GetType().Name}: {ex.Message}) — 정션으로 대체한다.");
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
        {
            output.WriteLine("cmd.exe를 시작할 수 없어 이 테스트를 건너뛴다.");
            return false;
        }
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null) return true;

        output.WriteLine($"정션 생성도 실패해 이 테스트를 건너뛴다(exit {process.ExitCode}): {stdout}{stderr}");
        return false;
    }
}
