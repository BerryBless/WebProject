using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Features.Attachments;

/// <summary>첨부 공개 GET. <c>/api</c> 밖에 있는 <b>읽기 전용</b> 엔드포인트다(공개 호스트의 글 본문과, 관리 호스트의 미리보기 iframe이 쓴다).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 무상태 정적 핸들러.</description></item>
/// <item><description><b>Memory Allocation:</b> DB 프로젝션 1행. 본체는 <c>TypedResults.Stream</c>이 만드는 <c>FileStreamHttpResult</c>가 <c>Response.Body</c>로
/// 버퍼링 복사(<c>CopyToAsync</c>)한다(<c>PhysicalFile</c>의 커널 sendfile 경로가 아니다) — 그래도 파일 전체를 메모리에 한 번에 올리지는 않는다.</description></item>
/// <item><description><b>Blocking:</b> 비동기 DB 조회 + 비동기 파일 전송. 이 앱은 <c>AddAuthentication</c>에 명시적 기본 쿠키 스킴을 등록하므로(<see cref="Infrastructure.Access.AuthServiceCollectionExtensions.AddAdminAuth"/>
/// 참조), 인증 미들웨어는 이 엔드포인트를 포함한 <b>모든</b> 요청에서 그 스킴을 평가한다 — 형식이 맞는 관리 세션 쿠키가 실려 오면 이 공개 엔드포인트에서도 세션 검증 DB 조회(행 1개)가 일어난다.
/// 다만 그 쿠키는 <c>__Host-</c> 접두사로 관리 호스트에 바인딩된 host-only 쿠키라 브라우저가 이 공개 호스트로는 애초에 보내지 않으므로,
/// 이 비용은 쿠키를 직접 조작해 보낸 요청에서만 발생한다(공개 속도 제한은 Plan 2B에서 이 표면을 마저 제한한다).</description></item>
/// </list>
/// 조회 키는 <c>id</c>뿐이다. <c>fileName</c>은 URL을 읽기 좋게 하는 장식이며 어떤 값이 와도 경로에 결합하지 않는다.
/// 응답은 스니핑 금지 + 자체 CSP(<c>default-src 'none'; sandbox</c>)로, 설령 이미지로 위장한 콘텐츠가 저장돼 있어도 문서로 실행되지 않는다 —
/// 이 두 헤더는 이 핸들러가 만드는 <b>모든</b> 응답(200·404)에 실린다. 파일을 여는 시점과 DB 조회 시점 사이의
/// TOCTOU 경쟁(삭제와 겹치면 <c>File.Exists</c> 확인 뒤 열기가 실패할 수 있었다)을 없애려고 <c>File.Exists</c> 대신 파일을 직접 열어 보고
/// 실패를 잡는다 — 그 결과 <see cref="FileSystemAttachmentStore.TryDelete"/>가 서빙 중인 파일과 경쟁해도 더 이상
/// 고아 파일로 남지 않는다(아래 <see cref="GetAsync"/> Concurrency 항목 참조).
/// </remarks>
public static class PublicAttachmentEndpoints
{
    // 64KB: 저장소의 다른 스트림 I/O와 같은 버퍼 크기 — 파일 하나를 메모리에 통째로 올리지 않으면서 시스템 호출 횟수를 줄인다.
    private const int BufferSize = 64 * 1024;

    /// <summary>공개 첨부 GET 라우트 패턴. 테스트 프로젝트의 <c>AccessMatrixTests.PublicAllowlist</c>에도 같은 문자열로 등록된다.</summary>
    public const string Pattern = "/attachments/{id:guid}/{fileName}";

    /// <summary>공개 첨부 GET·HEAD 엔드포인트를 <c>/api</c> 그룹 밖(전체 앱 루트)에 등록한다.</summary>
    /// <param name="app">엔드포인트를 등록할 <see cref="WebApplication"/>.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출되어 라우트를 등록한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 라우트 등록에 따른 시작 시 1회성 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// <c>MapGet</c>만 쓰면 HEAD가 405가 된다(실측, fix round 2 B5) — 캐시·링크 점검기가 HEAD로 존재만 확인하는 경우가 흔하므로
    /// <c>MapMethods</c>로 GET과 HEAD를 함께 등록한다.
    /// </remarks>
    public static void MapPublicAttachmentEndpoints(this WebApplication app)
    {
        app.MapMethods(Pattern, ["GET", "HEAD"], GetAsync).AllowAnonymous().WithName("GetAttachment");
    }

    /// <summary><paramref name="id"/>로 첨부를 찾아 파일을 스트리밍한다. <paramref name="fileName"/>은 무시한다.</summary>
    /// <param name="id">조회할 첨부의 Id(유일한 조회 키).</param>
    /// <param name="http">응답 헤더를 직접 쓰기 위한 <see cref="HttpContext"/>.</param>
    /// <param name="db">조회에 쓸 DbContext.</param>
    /// <param name="store">저장 경로를 실제 파일 시스템 경로로 바꾸는 저장소.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>파일이 있으면 200(스트리밍 본문, GET일 때만 — HEAD는 프레임워크가 본문을 비운다) 또는 <c>If-None-Match</c>가 일치하면 304(본문 없음),
    /// DB 행이 없거나 파일을 열 수 없으면 404.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다. 파일을 여는 <see cref="FileStream"/> 생성자 호출은 짧은 동기 I/O다(비동기 오버랩 I/O로 여는 핸들 자체를 만드는 단계는 동기적으로 끝난다).</description></item>
    /// <item><description><b>Memory Policy:</b> DB 조회는 <c>StoragePath</c>·<c>ContentType</c> 두 필드만 프로젝션한다. 본체는 <c>TypedResults.Stream</c>이 응답으로 버퍼링 복사하므로 파일 전체를 메모리에 한 번에 올리지 않는다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 존재 확인과 여는 시점을 분리하지 않고 <c>FileStream</c>을 직접 열어 실패를 잡는다(<c>File.Exists</c> 뒤에 열기가 실패하는 TOCTOU 경쟁이 없다, fix round 1 A2). 삭제와 경쟁하면(<see cref="AttachmentEndpoints.DeleteAsync"/> 참조) DB 행이 먼저 지워지므로 이 조회가 404가 되거나, DB 행이 아직 남아 있는 사이 파일이 지워졌으면(관리자가 볼륨에서 직접 지운 경우 포함) <see cref="FileNotFoundException"/>을 잡아 404가 된다 — 이 핸들러가 잘못된 내용을 돌려주는 경로는 없다. <see cref="FileShare.Delete"/>로 열기 때문에 이 핸들러가 스트리밍 중인 동안 <see cref="FileSystemAttachmentStore.TryDelete"/>가 같은 파일을 지워도(Windows에서) 공유 위반 없이 성공한다 — 삭제는 즉시 디렉터리 항목을 없애고(이후 요청은 404), 이미 열려 있는 이 핸들은 응답이 끝날 때까지 데이터를 계속 읽을 수 있다. Non-blocking: DB 조회는 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> GetAsync(Guid id, HttpContext http, AppDbContext db, FileSystemAttachmentStore store, CancellationToken ct)
    {
        // 이 핸들러가 내는 모든 응답(200·404)에 스니핑 금지·CSP를 건다 — 캐시 헤더만 200 전용이다(아래).
        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; sandbox";

        var row = await db.Attachments.AsNoTracking().Where(a => a.Id == id)
            .Select(a => new { a.StoragePath, a.ContentType, a.Sha256, a.CreatedAt }).SingleOrDefaultAsync(ct);
        if (row is null) return TypedResults.NotFound();
        var path = store.PhysicalPath(row.StoragePath);

        FileStream stream;
        try
        {
            // FileShare.Read | FileShare.Delete: 다른 스레드의 읽기도, 관리자의 삭제도 막지 않는다(Concurrency 항목 참조).
            // FileOptions.Asynchronous: 커널 비동기 I/O 경로로 연다. SequentialScan: 앞에서 뒤로 한 번만 읽는다는 OS 미리읽기 힌트.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return TypedResults.NotFound(); }
        catch (DirectoryNotFoundException) { return TypedResults.NotFound(); }

        // 스트림을 연 뒤부터는 결과를 만들어 반환할 때까지 예외가 나도 핸들이 새지 않도록 감싼다 — "열기와 반환 사이에
        // 아무것도 못 던지게" 구조적으로 보장하는 대신, 던지면 반드시 스트림을 정리하고 다시 던지는 형태를 택했다: Cache-Control은
        // 404 경로에는 절대 실리면 안 되므로 스트림을 연 뒤에만 설정해야 하고, 그 순서 제약 자체는 없앨 수 없기 때문이다).
        try
        {
            headers.CacheControl = "public, max-age=31536000, immutable"; // 내용 주소: 같은 id의 내용은 바뀌지 않는다. 캐시된 404가 나중 업로드를 가릴 수 있으므로 404에는 붙이지 않는다.
            // 강한 ETag: 내용 자체(제거 후 바이트의 SHA-256)가 곧 검증자이므로 별도로 다시 해시하지 않고도 조건부 요청을 재검증할 수 있다.
            // Last-Modified는 보조 신호로 곁들인다(오래된 캐시·프록시가 ETag를 못 볼 때의 대비).
            var entityTag = new EntityTagHeaderValue("\"" + row.Sha256 + "\"");
            return TypedResults.Stream(stream, row.ContentType, lastModified: row.CreatedAt, entityTag: entityTag); // Range 처리는 켜지 않는다(enableRangeProcessing 기본값 false).
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}
