using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Features.Attachments;

/// <summary>첨부 공개 GET. <c>/api</c> 밖에 있는 <b>읽기 전용</b> 엔드포인트다(공개 호스트의 글 본문과, 관리 호스트의 미리보기 iframe이 쓴다).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 무상태 정적 핸들러.</description></item>
/// <item><description><b>Memory Allocation:</b> DB 프로젝션 1행. 본체는 <c>PhysicalFile</c> 결과가 스트리밍한다(파일을 메모리에 올리지 않는다).</description></item>
/// <item><description><b>Blocking:</b> 비동기 DB 조회 + 비동기 파일 전송. 이 앱은 <c>AddAuthentication</c>에 명시적 기본 쿠키 스킴을 등록하므로(<see cref="Infrastructure.Access.AuthServiceCollectionExtensions.AddAdminAuth"/>
/// 참조), 인증 미들웨어는 이 엔드포인트를 포함한 <b>모든</b> 요청에서 그 스킴을 평가한다 — 형식이 맞는 관리 세션 쿠키가 실려 오면 이 공개 엔드포인트에서도 세션 검증 DB 조회(행 1개)가 일어난다.
/// 다만 그 쿠키는 <c>__Host-</c> 접두사로 관리 호스트에 바인딩된 host-only 쿠키라 브라우저가 이 공개 호스트로는 애초에 보내지 않으므로,
/// 이 비용은 쿠키를 직접 조작해 보낸 요청에서만 발생한다(공개 속도 제한은 Plan 2B에서 이 표면을 마저 제한한다).</description></item>
/// </list>
/// 조회 키는 <c>id</c>뿐이다. <c>fileName</c>은 URL을 읽기 좋게 하는 장식이며 어떤 값이 와도 경로에 결합하지 않는다.
/// 응답은 스니핑 금지 + 자체 CSP(<c>default-src 'none'; sandbox</c>)로, 설령 이미지로 위장한 콘텐츠가 저장돼 있어도 문서로 실행되지 않는다.
/// </remarks>
public static class PublicAttachmentEndpoints
{
    /// <summary>공개 첨부 GET 라우트 패턴. 테스트 프로젝트의 <c>AccessMatrixTests.PublicAllowlist</c>에도 같은 문자열로 등록된다.</summary>
    public const string Pattern = "/attachments/{id:guid}/{fileName}";

    /// <summary>공개 첨부 GET 엔드포인트를 <c>/api</c> 그룹 밖(전체 앱 루트)에 등록한다.</summary>
    /// <param name="app">엔드포인트를 등록할 <see cref="WebApplication"/>.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출되어 라우트를 등록한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 라우트 등록에 따른 시작 시 1회성 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static void MapPublicAttachmentEndpoints(this WebApplication app)
    {
        app.MapGet(Pattern, GetAsync).AllowAnonymous().WithName("GetAttachment");
    }

    /// <summary><paramref name="id"/>로 첨부를 찾아 파일을 스트리밍한다. <paramref name="fileName"/>은 무시한다.</summary>
    /// <param name="id">조회할 첨부의 Id(유일한 조회 키).</param>
    /// <param name="http">응답 헤더를 직접 쓰기 위한 <see cref="HttpContext"/>.</param>
    /// <param name="db">조회에 쓸 DbContext.</param>
    /// <param name="store">저장 경로를 실제 파일 시스템 경로로 바꾸는 저장소.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>파일이 있으면 200(스트리밍 본문), DB 행이 없거나 파일이 없으면 404.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다. 파일 존재 확인(<see cref="File.Exists(string?)"/>)은 짧은 동기 I/O다.</description></item>
    /// <item><description><b>Memory Policy:</b> DB 조회는 <c>StoragePath</c>·<c>ContentType</c> 두 필드만 프로젝션한다. 본체는 <c>PhysicalFile</c>이 커널 sendfile 경로로 스트리밍하므로 메모리에 올리지 않는다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 삭제와 경쟁하면(<see cref="AttachmentEndpoints.DeleteAsync"/> 참조) DB 행이 먼저 지워지므로 이 조회가 404가 되거나, 파일이 아직 열려 있어 삭제가 실패해 고아 파일로 남는다 — 둘 중 무엇이든 이 핸들러가 잘못된 내용을 돌려주지는 않는다. Non-blocking: DB 조회는 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> GetAsync(Guid id, HttpContext http, AppDbContext db, FileSystemAttachmentStore store, CancellationToken ct)
    {
        var row = await db.Attachments.AsNoTracking().Where(a => a.Id == id)
            .Select(a => new { a.StoragePath, a.ContentType }).SingleOrDefaultAsync(ct);
        if (row is null) return TypedResults.NotFound();
        var path = store.PhysicalPath(row.StoragePath);
        if (!File.Exists(path)) return TypedResults.NotFound();

        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        headers.CacheControl = "public, max-age=31536000, immutable"; // 내용 주소: 같은 id의 내용은 바뀌지 않는다
        return TypedResults.PhysicalFile(path, row.ContentType);
    }
}
