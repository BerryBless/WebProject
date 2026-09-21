using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Infrastructure.Storage;
using PortfolioBlog.Api.Infrastructure.Web;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>첨부 업로드·목록·삭제(관리)와 공개 GET 통합 테스트.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 첨부는 내용 주소로 중복 제거되므로 같은 픽스처를 올리는 테스트끼리 서로의 결과(201/200)를 바꾼다.
/// 그래서 <b>테스트마다 격리된 <see cref="ApiFactory"/></b>(자체 DB + 자체 임시 첨부 폴더)를 만든다.</description></item>
/// <item><description><b>Memory Allocation:</b> 크기 초과 테스트가 10MB+1바이트 버퍼 하나를 만든다.</description></item>
/// <item><description><b>Blocking:</b> 비동기. 실제 PostgreSQL 컨테이너와 로컬 임시 디렉터리를 쓴다. 팩토리는 <c>using</c>으로 해제되어 임시 폴더를 지운다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class AttachmentEndpointsTests(PostgresContainerFixture pg)
{
    private static readonly Dictionary<string, string?> NoOverrides = new();

    /// <summary>공개 조회 연결의 <c>statement_timeout</c>을 200ms로 줄이는 설정(잠금 대기가 시간 제한에 걸리는지 짧게 관측하기 위한 값).</summary>
    private static readonly Dictionary<string, string?> FastPublicTimeout = new() { ["Public:StatementTimeoutMs"] = "200" };

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name));

    private static MultipartFormDataContent Form(byte[] bytes, string fileName, string contentType = "application/octet-stream")
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { part, "file", fileName } };
    }

    private static async Task<AttachmentDto> UploadAsync(HttpClient client, byte[] bytes, string fileName, HttpStatusCode expected = HttpStatusCode.Created)
    {
        using var form = Form(bytes, fileName);
        using var res = await client.PostAsync("/api/attachments", form);
        Assert.Equal(expected, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<AttachmentDto>(TestJson.Options))!;
    }

    private static async Task<HttpStatusCode> UploadStatusAsync(HttpClient client, MultipartFormDataContent form)
    {
        using (form)
        {
            using var res = await client.PostAsync("/api/attachments", form);
            return res.StatusCode;
        }
    }

    // DB 행의 StoragePath를 실제 파일 시스템 경로로 바꾼다. 스코프는 조회 즉시 해제한다.
    private static async Task<string> PhysicalPathAsync(ApiFactory factory, Guid id)
    {
        await using var scope = factory.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Attachments.AsNoTracking().SingleAsync(a => a.Id == id);
        return scope.ServiceProvider.GetRequiredService<FileSystemAttachmentStore>().PhysicalPath(row.StoragePath);
    }

    // 프로덕션 핸들은 FileShare.Read | FileShare.Delete로 열리므로 File.Delete는 핸들이 새고 있어도 성공해 버려
    // "핸들이 released됐다"를 증명하지 못한다(File.Delete로는 절대 실패할 수 없는 단언이었다). FileShare.None으로
    // 배타적으로 열어야만 살아 있는 핸들과 진짜로 충돌해 IOException을 던진다. TestServer가 응답을 완료로 표시하는
    // 시점과 서버 쪽 스트림이 실제로 Dispose되는 시점 사이에 짧은 간극이 있을 수 있어 몇 차례 재시도한다.
    private static async Task AssertHandleReleasedAsync(string physical)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var fs = new FileStream(physical, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                await Task.Delay(50);
            }
        }
        Assert.Fail($"응답이 끝난 뒤에도 파일을 배타적으로 열 수 없다 — 핸들이 새고 있다: {last}");
    }

    /// <summary>업로드하면 메타데이터가 제거된 파일이 저장되고, 공개 호스트에서 익명으로 받을 수 있으며, 응답 헤더가 스니핑·실행을 막는다.</summary>
    [Fact]
    public async Task Upload_StripsMetadata_AndServesPubliclyWithHardenedHeaders()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var original = Fixture("exif-gps.jpg");
        var dto = await UploadAsync(admin, original, "휴가 사진.JPG");

        Assert.Equal("image/jpeg", dto.ContentType);
        Assert.Equal("휴가 사진.jpg", dto.FileName); // 확장자는 시그니처에서 다시 정한다
        Assert.Equal(64, dto.Sha256.Length);
        Assert.StartsWith($"/attachments/{dto.Id}/", dto.Url, StringComparison.Ordinal);
        Assert.True(dto.SizeBytes < original.Length);

        using var visitor = factory.CreatePublicClient();
        using var res = await visitor.GetAsync(dto.Url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/jpeg", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("default-src 'none'; sandbox", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(res.Headers.CacheControl?.Public);
        Assert.Contains("immutable", res.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        var served = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(dto.SizeBytes, served.Length);
        Assert.Equal(dto.Sha256, Convert.ToHexStringLower(SHA256.HashData(served)));
        foreach (var secret in new[] { "Exif", "SpikeCam", "secret" })
        {
            Assert.True(served.AsSpan().IndexOf(Encoding.ASCII.GetBytes(secret)) < 0, $"공개 응답에 '{secret}'가 남아 있다");
        }
    }

    /// <summary>네 형식 모두 받아들이고, Content-Type과 확장자는 시그니처에서 나온다(클라이언트가 보낸 이름·형식은 무시).</summary>
    [Theory]
    [InlineData("exif-text.png", "image/png", "png")]
    [InlineData("exif-xmp.webp", "image/webp", "webp")]
    [InlineData("comment-animated.gif", "image/gif", "gif")]
    [InlineData("progressive-trailing.jpg", "image/jpeg", "jpg")]
    public async Task Upload_AcceptsEachFormat(string fixture, string contentType, string extension)
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var dto = await UploadAsync(admin, Fixture(fixture), "wrong-name.exe");
        Assert.Equal(contentType, dto.ContentType);
        Assert.Equal("wrong-name." + extension, dto.FileName);
    }

    /// <summary>같은 내용을 다시 올리면 새로 만들지 않고 기존 첨부를 200으로 돌려준다. 이름이 달라도, 덧붙은 바이트만 달라도(제거 후 같아지므로) 같은 첨부다.</summary>
    [Fact]
    public async Task Upload_SameContent_ReturnsExisting()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var png = Fixture("exif-text.png");
        var first = await UploadAsync(admin, png, "a.png");
        var again = await UploadAsync(admin, png, "b.png", HttpStatusCode.OK);
        var padded = await UploadAsync(admin, [.. png, .. Encoding.ASCII.GetBytes("trailing bytes after IEND")], "c.png", HttpStatusCode.OK);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Id, padded.Id);
        Assert.Equal("a.png", again.FileName);
    }

    /// <summary>이미지가 아니거나 구조가 깨진 파일은 415, 빈 파일·필드 누락은 400, 10MB 초과는 413이다 — 어느 것도 500이 아니다.</summary>
    [Fact]
    public async Task Upload_RejectsBadInput_WithSpecificStatus()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var png = Fixture("exif-text.png");

        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await UploadStatusAsync(admin, Form(svg, "x.svg", "image/svg+xml")));
        var html = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await UploadStatusAsync(admin, Form(html, "x.png", "image/png")));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await UploadStatusAsync(admin, Form(png[..(png.Length / 2)], "cut.png")));
        Assert.Equal(HttpStatusCode.BadRequest, await UploadStatusAsync(admin, Form([], "empty.png")));
        Assert.Equal(HttpStatusCode.BadRequest, await UploadStatusAsync(admin, new MultipartFormDataContent { { new StringContent("x"), "other" } }));

        var tooBig = new byte[AttachmentOptions.MaxBytes + 1];
        png.CopyTo(tooBig, 0); // 시그니처는 PNG다 — 크기 검사가 형식 검사보다 먼저여야 한다
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await UploadStatusAsync(admin, Form(tooBig, "big.png")));
    }

    /// <summary>양성 대조군: 로그인한 세션으로 <c>file</c> 파트가 없는 multipart를 보내면 400이 나고, 그 본문에는
    /// 실제로 핸들러가 만드는 필드 누락 문구가 있다 — <c>AccessMatrixTests</c>의 "핸들러가 호출되지 않았다" 단언이 같은 문구의 부재를
    /// 보고 있다는 것이 의미 있는 검사임을 증명한다(그 문구가 애초에 어떤 응답에도 나타나지 않는 죽은 문자열이 아님을 확인).</summary>
    [Fact]
    public async Task Upload_MissingFileField_Returns400_WithFileFieldMessage()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        using var form = new MultipartFormDataContent { { new StringContent("x"), "other" } };

        using var res = await admin.PostAsync("/api/attachments", form);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("multipart 필드 'file'", body, StringComparison.Ordinal);
    }

    /// <summary>업로드한 파일 이름은 경로 조각·제어문자를 걷어 낸 표시용 이름이 되고, 공개 URL의 파일 이름을 아무리 바꿔도 같은 파일이 나온다(경로에 쓰이지 않는다).</summary>
    [Fact]
    public async Task FileName_IsDisplayOnly_NeverAPath()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var dto = await UploadAsync(admin, Fixture("exif-xmp.webp"), "..\\..\\etc/pass\twd.webp");
        Assert.Equal("passwd.webp", dto.FileName);

        using var visitor = factory.CreatePublicClient();
        var expected = await visitor.GetByteArrayAsync(dto.Url);
        foreach (var name in new[] { "anything.webp", "..%2F..%2Fappsettings.json", "x" })
        {
            using var res = await visitor.GetAsync($"/attachments/{dto.Id}/{name}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(expected, await res.Content.ReadAsByteArrayAsync());
        }
        using var missing = await visitor.GetAsync($"/attachments/{Guid.NewGuid()}/x.webp");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>목록은 최신순 페이지네이션, 삭제는 DB 행과 디스크 파일을 함께 지우고 공개 URL은 404가 된다.</summary>
    [Fact]
    public async Task List_And_Delete()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var older = await UploadAsync(admin, Fixture("exif-text.png"), "older.png");
        var newer = await UploadAsync(admin, Fixture("comment-animated.gif"), "newer.gif");

        var page = await admin.GetFromJsonAsync<PagedAttachmentsDto>("/api/attachments?skip=0&take=1", TestJson.Options);
        Assert.Equal(2, page!.Total);
        Assert.Equal(newer.Id, page.Items.Single().Id);
        using (var bad = await admin.GetAsync("/api/attachments?take=0")) Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        string physical;
        await using (var scope = factory.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Attachments.AsNoTracking().SingleAsync(a => a.Id == newer.Id);
            physical = scope.ServiceProvider.GetRequiredService<FileSystemAttachmentStore>().PhysicalPath(row.StoragePath);
        }
        Assert.True(File.Exists(physical));

        using (var res = await admin.DeleteAsync($"/api/attachments/{newer.Id}")) Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.False(File.Exists(physical));
        using (var again = await admin.DeleteAsync($"/api/attachments/{newer.Id}")) Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        using var visitor = factory.CreatePublicClient();
        using (var gone = await visitor.GetAsync(newer.Url)) Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using (var kept = await visitor.GetAsync(older.Url)) Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
    }

    /// <summary>공개 GET은 읽기 전용이다: 다른 메서드는 405이고 업로드 경로는 공개 호스트에 존재하지 않는다.</summary>
    [Fact]
    public async Task PublicSurface_IsReadOnly()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var visitor = factory.CreatePublicClient();
        using var form = Form(Fixture("exif-text.png"), "x.png");
        using var post = await visitor.PostAsync($"/attachments/{Guid.NewGuid()}/x.png", form);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await UploadStatusAsync(visitor, Form(Fixture("exif-text.png"), "x.png")));
    }

    /// <summary>파일 이름 길이 제한이 서러게이트 쌍 한가운데를 자르는 위치라도 500이 아니라 201이 나오고, 반환된 파일 이름에는
    /// 홀로 남은 서러게이트가 없으며, 반환된 URL은 <see cref="UrlPolicy.IsAllowedImage"/>를 통과한다.</summary>
    [Fact]
    public async Task Upload_FileNameTruncationSplitsSurrogatePair_Returns201_NotServerError()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        // 재현 케이스: 249개의 'a' + 이모지(서러게이트 쌍) + "bbbb.webp" — 255자 길이 제한이 정확히 이모지 한가운데를 자른다.
        var uploadedName = new string('a', 249) + "\U0001F600" + "bbbb.webp";

        var dto = await UploadAsync(admin, Fixture("exif-xmp.webp"), uploadedName);

        for (var i = 0; i < dto.FileName.Length; i++)
        {
            if (char.IsHighSurrogate(dto.FileName[i]))
            {
                Assert.True(i + 1 < dto.FileName.Length && char.IsLowSurrogate(dto.FileName[i + 1]), $"홀로 남은 상위 서러게이트: \"{dto.FileName}\"");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(dto.FileName[i]), $"홀로 남은 하위 서러게이트: \"{dto.FileName}\"");
            }
        }
        Assert.True(UrlPolicy.IsAllowedImage(dto.Url), $"반환된 URL이 UrlPolicy.IsAllowedImage를 통과하지 못했다: {dto.Url}");
    }

    /// <summary>공개 GET 200 응답에 내용 주소 SHA-256을 강한 ETag로, 업로드 시각을 Last-Modified로 담는다. 그 ETag를 If-None-Match로
    /// 다시 보내면 304(빈 본문)가 오고 nosniff·CSP는 그대로 실린다(내용 자체가 검증자이므로 재해시 없이 캐시를 재검증할 수 있다).
    /// 304 응답이 끝난 뒤에도 파일 핸들이 새지 않는지(배타적으로 다시 열 수 있는지)까지 확인한다.</summary>
    [Fact]
    public async Task PublicGet_HasEtagAndLastModified_AndConditionalGetReturns304()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var dto = await UploadAsync(admin, Fixture("exif-text.png"), "a.png");

        using var visitor = factory.CreatePublicClient();
        using var res = await visitor.GetAsync(dto.Url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var etag = res.Headers.ETag;
        Assert.NotNull(etag);
        Assert.False(etag!.IsWeak, "SHA-256이 곧 내용이므로 약한 ETag가 아니라 강한 ETag여야 한다");
        Assert.Equal($"\"{dto.Sha256}\"", etag.Tag);
        Assert.NotNull(res.Content.Headers.LastModified);

        using var req = new HttpRequestMessage(HttpMethod.Get, dto.Url);
        req.Headers.IfNoneMatch.Add(etag);
        using var conditional = await visitor.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
        Assert.Equal("nosniff", conditional.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("default-src 'none'; sandbox", conditional.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Empty(await conditional.Content.ReadAsByteArrayAsync());

        await AssertHandleReleasedAsync(await PhysicalPathAsync(factory, dto.Id));
    }

    /// <summary>공개 GET 라우트는 HEAD도 받는다: 200에 GET과 같은 헤더(Content-Type·Content-Length·nosniff·CSP·Cache-Control·ETag)가 실리지만
    /// 본문은 비어 있고, 핸들은 응답이 끝나면 해제된다(응답이 끝난 뒤 그 파일을 배타적으로(<see cref="FileShare.None"/>) 다시 열 수 있는지로 확인한다 —
    /// 프로덕션 핸들은 <see cref="FileShare.Read"/> | <see cref="FileShare.Delete"/>로 열리므로 <c>File.Delete</c>는 핸들이 새고 있어도 성공해 버려 증거가 되지 못한다).</summary>
    [Fact]
    public async Task PublicGet_Head_ReturnsHeadersWithoutBody_AndReleasesHandle()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var dto = await UploadAsync(admin, Fixture("exif-text.png"), "a.png");

        using var visitor = factory.CreatePublicClient();
        using var getRes = await visitor.GetAsync(dto.Url);
        var expectedLength = getRes.Content.Headers.ContentLength;

        using var headReq = new HttpRequestMessage(HttpMethod.Head, dto.Url);
        using var headRes = await visitor.SendAsync(headReq);

        Assert.Equal(HttpStatusCode.OK, headRes.StatusCode);
        Assert.Equal("image/png", headRes.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedLength, headRes.Content.Headers.ContentLength);
        Assert.Equal("nosniff", headRes.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("default-src 'none'; sandbox", headRes.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(headRes.Headers.CacheControl?.Public);
        Assert.NotNull(headRes.Headers.ETag);
        var headBody = await headRes.Content.ReadAsByteArrayAsync();
        Assert.Empty(headBody);

        await AssertHandleReleasedAsync(await PhysicalPathAsync(factory, dto.Id));
    }

    /// <summary>DB 행은 있지만 디스크 파일이 없으면(관리자가 볼륨에서 직접 지운 경우 등) 500이 아니라 404다.</summary>
    [Fact]
    public async Task PublicGet_WhenFileIsMissingOnDisk_Returns404()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var dto = await UploadAsync(admin, Fixture("exif-text.png"), "a.png");

        string physical;
        await using (var scope = factory.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Attachments.AsNoTracking().SingleAsync(a => a.Id == dto.Id);
            physical = scope.ServiceProvider.GetRequiredService<FileSystemAttachmentStore>().PhysicalPath(row.StoragePath);
        }
        File.Delete(physical); // 볼륨에서 직접 지움 — DB 행은 남아 있다

        using var visitor = factory.CreatePublicClient();
        using var res = await visitor.GetAsync(dto.Url);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("default-src 'none'; sandbox", res.Headers.GetValues("Content-Security-Policy").Single());
    }

    /// <summary><c>Attachments:RootPath</c>가 구분자로 끝나도(운영 compose에서 자연스러운 오타, 예: <c>/data/attachments/</c>)
    /// 업로드가 201로 성공하고 저장된 파일을 공개 GET이 200으로 서빙하는지 검증한다(F1 회귀 — 끝 구분자가 남으면
    /// <c>PhysicalPath</c>의 접두사 비교가 영원히 실패해 모든 업로드·공개 GET이 500이 됐었다).</summary>
    [Fact]
    public async Task Upload_RootPathEndsWithDirectorySeparator_StillWorks() =>
        await AssertUploadAndPublicGetSucceed(Path.DirectorySeparatorChar);

    /// <summary><c>Attachments:RootPath</c>가 대체 구분자(Windows에서는 <c>/</c>)로 끝나도 F1 회귀가 없는지 검증한다.</summary>
    [Fact]
    public async Task Upload_RootPathEndsWithAltDirectorySeparator_StillWorks() =>
        await AssertUploadAndPublicGetSucceed(Path.AltDirectorySeparatorChar);

    private async Task AssertUploadAndPublicGetSucceed(char rootTrailingSeparator)
    {
        using var factory = new ApiFactory(pg, NoOverrides, rootTrailingSeparator);
        using var admin = await factory.CreateLoggedInClientAsync();
        var dto = await UploadAsync(admin, Fixture("exif-text.png"), "a.png");

        using var visitor = factory.CreatePublicClient();
        using var res = await visitor.GetAsync(dto.Url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    /// <summary>공개 첨부 GET은 관리 풀이 아니라 공개 조회 연결을 쓴다: <c>Attachments</c>가 <c>ACCESS EXCLUSIVE</c>로 잠긴 동안 요청하면
    /// 잠금이 풀릴 때까지 매달리지 않고 <c>statement_timeout</c>(이 테스트에서는 200ms)에 SqlState 57014로 끊겨 503 + <c>Retry-After</c>가 온다.
    /// 핸들러가 관리 컨텍스트를 쓰면(<c>statement_timeout</c> 없음) 이 요청은 잠금이 풀릴 때까지 기다려 아래 5초 유계 대기에서 실패한다 —
    /// 그 유계 대기가 있어야 사보타주 상태의 테스트가 잠금 해제까지 매달리지 않는다.</summary>
    [Fact]
    public async Task PublicGet_WhenTheTableIsLocked_Returns503WithRetryAfter()
    {
        using var factory = new ApiFactory(pg, FastPublicTimeout);
        using var admin = await factory.CreateLoggedInClientAsync();
        var dto = await UploadAsync(admin, Fixture("exif-text.png"), "a.png");

        // 잠금은 명시적 트랜잭션 안에서만 유지된다(LOCK TABLE은 트랜잭션이 끝나면 풀린다). 관리 연결 문자열을 쓰는
        // 별도 연결이라 앱의 두 풀(관리·공개)과 물리 연결을 공유하지 않는다 — 앱 요청이 이 잠금을 자기 연결로 우회할 수 없다.
        await using var holder = new NpgsqlConnection(factory.ConnectionString);
        await holder.OpenAsync();
        var tx = await holder.BeginTransactionAsync();
        try
        {
            await using (var lockCmd = new NpgsqlCommand("LOCK TABLE \"Attachments\" IN ACCESS EXCLUSIVE MODE", holder, tx))
            {
                await lockCmd.ExecuteNonQueryAsync();
            }

            using var visitor = factory.CreatePublicClient();
            // CancellationTokenSource(5초): 200ms 제한이 실제로 걸렸다면 훨씬 먼저 끝난다. 걸리지 않은 구현에서 이 대기가
            // 테스트를 잠금 해제 시점까지(= finally까지) 붙잡아 교착하는 것을 막는 상한이다.
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var res = await visitor.GetAsync(dto.Url, bounded.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(OverloadExceptionHandler.RetryAfterSeconds), res.Headers.RetryAfter?.Delta);
        }
        finally
        {
            // 잠금 연결은 어떤 경로로 빠져나가도 되돌리고 닫는다(테스트 DB는 팩토리와 함께 버려지지만, 잠금이 남으면
            // 같은 팩토리의 뒤이은 정리 작업이 막힐 수 있다).
            await tx.RollbackAsync();
            await tx.DisposeAsync();
        }
    }

    /// <summary>임시 파일이 남지 않는다(성공·거부 어느 경로든).</summary>
    [Fact]
    public async Task Upload_LeavesNoTempFiles()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        await UploadAsync(admin, Fixture("exif-gps.jpg"), "t.jpg");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await UploadStatusAsync(admin, Form(Encoding.UTF8.GetBytes("not an image"), "t.png")));

        var temp = Path.Combine(factory.AttachmentsRoot, ".tmp");
        Assert.True(!Directory.Exists(temp) || !Directory.EnumerateFiles(temp).Any());
    }
}
