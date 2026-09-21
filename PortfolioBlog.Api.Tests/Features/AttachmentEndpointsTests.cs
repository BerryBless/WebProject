using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Infrastructure.Storage;
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
    /// 홀로 남은 서러게이트가 없으며, 반환된 URL은 <see cref="UrlPolicy.IsAllowedImage"/>를 통과한다(fix round 1, A1).</summary>
    [Fact]
    public async Task Upload_FileNameTruncationSplitsSurrogatePair_Returns201_NotServerError()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        // 리뷰어의 재현: 249개의 'a' + 이모지(서러게이트 쌍) + "bbbb.webp" — 255자 길이 제한이 정확히 이모지 한가운데를 자른다.
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

    /// <summary>DB 행은 있지만 디스크 파일이 없으면(관리자가 볼륨에서 직접 지운 경우 등) 500이 아니라 404다(fix round 1, A2).</summary>
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
