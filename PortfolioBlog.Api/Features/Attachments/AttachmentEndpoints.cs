using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Storage;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Features.Attachments;

/// <summary>첨부 관리 API(업로드·목록·삭제). 보호된 <c>/api</c> 그룹 안에 있어 본문을 읽기 전에 호스트·IP·CSRF 헤더·Origin·세션 검사를 통과한 요청만 도달한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 무상태 정적 핸들러. 저장소는 Thread-safe 싱글턴, DbContext는 요청 스코프.</description></item>
/// <item><description><b>Memory Allocation:</b> 업로드는 프레임워크가 64KB를 넘는 폼 파일을 디스크로 버퍼링하고, 저장소가 64KB 단위로 옮긴다. 10MB를 메모리에 올리지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 수신·DB는 비동기. 메타데이터 제거·해시는 임시 파일 동기 I/O.</description></item>
/// </list>
/// antiforgery: 최소 API는 <c>IFormFile</c> 바인딩에 프레임워크 antiforgery 검증을 요구한다. 이 앱의 CSRF 방어는 미들웨어의
/// <c>X-Requested-With</c> + <c>Origin</c> 검사와 <c>SameSite=Strict</c> 쿠키이므로 이 엔드포인트에서만 <c>DisableAntiforgery()</c>로 대체한다(전역 비활성화가 아니다).
/// </remarks>
public static class AttachmentEndpoints
{
    /// <summary>목록 조회에서 <c>take</c> 쿼리 값이 없을 때 기본으로 가져올 건수.</summary>
    public const int DefaultTake = 50;

    /// <summary>목록 조회에서 <c>take</c> 쿼리 값이 가질 수 있는 최대값.</summary>
    public const int MaxTake = 200;

    /// <summary><c>/attachments</c> 그룹을 만들고 업로드·목록·삭제 엔드포인트를 등록한다.</summary>
    /// <param name="api"><c>/api</c> 루트 그룹.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출되어 라우트를 등록한다. 등록되는 엔드포인트 델리게이트는 요청마다 병렬로 안전하게 호출된다(공유 가변 상태 없음, 의존성은 요청 스코프로 주입).</description></item>
    /// <item><description><b>Memory Allocation:</b> 라우트 그룹·엔드포인트 등록에 따른 시작 시 1회성 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static void MapAttachmentEndpoints(this RouteGroupBuilder api)
    {
        var attachments = api.MapGroup("/attachments");
        attachments.MapGet("", ListAsync).WithName("ListAttachments");
        attachments.MapPost("", UploadAsync).DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(AttachmentOptions.MaxBytes + 1_048_576))
            .WithMetadata(new RateLimitMetadata(RateLimitPolicy.Upload))
            .WithName("UploadAttachment");
        attachments.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteAttachment");
    }

    /// <summary><see cref="Attachment"/> 엔티티를 응답용 <see cref="AttachmentDto"/>로 바꾼다.</summary>
    /// <param name="a">변환할 엔티티.</param>
    /// <returns>파일 이름을 URL 인코딩해 넣은 공개 URL을 포함한 DTO.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 인자만으로 새 불변 record를 만든다.</description></item>
    /// <item><description><b>Memory Allocation:</b> DTO 1개와 URL 인코딩된 파일 이름 문자열 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    internal static AttachmentDto ToDto(Attachment a) =>
        new(a.Id, $"/attachments/{a.Id}/{Uri.EscapeDataString(a.FileName)}", a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.CreatedAt);

    /// <summary>첨부 목록을 최신순으로 페이지네이션해 조회한다.</summary>
    /// <param name="db">조회에 쓸 DbContext.</param>
    /// <param name="skip">건너뛸 건수(0 이상, 기본 0).</param>
    /// <param name="take">가져올 건수(1~<see cref="MaxTake"/>, 기본 <see cref="DefaultTake"/>).</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>페이지네이션된 목록과 전체 건수를 담은 200, 또는 잘못된 쿼리 값에 대한 400.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> <c>AsNoTracking()</c> 프로젝션 없이 엔티티를 <paramref name="take"/> 건수만큼 로드한다(첨부는 본문 문자열이 없어 목록 크기가 작다).</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 요청 스코프 의존성만 사용한다. Non-blocking: 개수 조회와 목록 조회를 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> ListAsync(AppDbContext db, int? skip, int? take, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (skip is < 0) errors.Add("skip", "skip은 0 이상이어야 합니다.");
        if (take is < 1 or > MaxTake) errors.Add("take", $"take는 1~{MaxTake}여야 합니다.");
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        var total = await db.Attachments.CountAsync(ct);
        var rows = await db.Attachments.AsNoTracking()
            .OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
            .Skip(skip ?? 0).Take(take ?? DefaultTake).ToListAsync(ct);
        return TypedResults.Ok(new PagedAttachmentsDto(rows.Select(ToDto).ToArray(), total));
    }

    /// <summary>multipart 필드 <c>file</c>의 이미지를 크기 한도·시그니처 판정·메타데이터 제거를 거쳐 저장하고, 같은 내용이 이미 있으면 기존 첨부를 돌려준다.</summary>
    /// <param name="file">바인딩된 업로드 파일(필드명 <c>file</c>). 비어 있거나 없으면 400.</param>
    /// <param name="db">저장에 쓸 DbContext.</param>
    /// <param name="store">시그니처 판정·메타데이터 제거·내용 주소 저장을 수행하는 싱글턴.</param>
    /// <param name="loggers">감사 로그(id·sha256·크기만) 기록용 로거 팩토리.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>새로 저장하면 201, 같은 내용이 이미 있으면 200, 필드 누락·빈 파일은 400, 10MB 초과는 413, 지원하지 않는 형식·손상된 구조는 415, 같은 내용의 잠금 대기가 10초를 넘으면 503(Retry-After).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다. <see cref="FileSystemAttachmentStore.SaveAsync"/>의 동기 구간(메타데이터 제거·해시)이 이 스레드를 짧게 막는다.</description></item>
    /// <item><description><b>Memory Policy:</b> 업로드 본문은 프레임워크가 디스크로 버퍼링하고, 저장소가 64KB 단위로 옮긴다. 10MB를 메모리에 올리지 않는다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 무거운 일(수신·메타데이터 제거·해시)은 <see cref="AttachmentLock"/> 밖에서 끝낸 뒤, 같은 내용(sha256)의 행 삽입은
    /// <see cref="AttachmentLock"/> 세션 잠금 안에서 삭제·청소 잡과 직렬화된다 — 잠금을 기다리는 사이 같은 내용이 지워졌으면 잠금 안에서 파일 존재를 다시 확인해 없으면
    /// <see cref="IFormFile.OpenReadStream"/>을 다시 열어 재저장한다(<c>IFormFile</c>은 프레임워크가 버퍼링해 둔 것이라 다시 열 수 있다). 잠금 대기가 10초를 넘으면
    /// SqlState 55P03 → <c>OverloadExceptionHandler</c>가 503으로 바꾼다. Non-blocking: DB 호출은 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> UploadAsync(IFormFile? file, AppDbContext db, FileSystemAttachmentStore store, ILoggerFactory loggers, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["multipart 필드 'file'에 비어 있지 않은 이미지가 필요합니다."] });
        }
        if (file.Length > AttachmentOptions.MaxBytes) return TooLarge();

        StoredImage stored;
        try
        {
            await using var upload = file.OpenReadStream();
            stored = await store.SaveAsync(upload, ct);
        }
        catch (AttachmentTooLargeException) { return TooLarge(); }
        catch (UnsupportedImageException ex)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "지원하지 않는 이미지", detail: ex.Message);
        }

        // 무거운 일(수신·메타데이터 제거·해시)은 잠금 밖에서 끝냈다. 잠금 안에서는 "파일 확인 + 행 조회/삽입"만 한다.
        await using (await AttachmentLock.HoldAsync(db, stored.Sha256, ct))
        {
            if (!store.Exists(stored.StoragePath))
            {
                // 잠금을 기다리는 사이 같은 내용의 삭제·청소가 파일을 지웠다. IFormFile은 프레임워크가 버퍼링해 둔 것이라 다시 열 수 있다.
                await using var again = file.OpenReadStream();
                stored = await store.SaveAsync(again, ct);
            }

            var existing = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Sha256 == stored.Sha256, ct);
            if (existing is not null) return TypedResults.Ok(ToDto(existing));

            var attachment = new Attachment
            {
                FileName = DisplayName(file.FileName, stored.Kind),
                ContentType = ImageSignature.ContentType(stored.Kind),
                SizeBytes = stored.SizeBytes, StoragePath = stored.StoragePath, Sha256 = stored.Sha256, CreatedAt = DbClock.UtcNow(),
            };
            db.Attachments.Add(attachment);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: DbConflict.UniqueViolation })
            {
                // 잠금 아래에서는 일어나지 않아야 한다. 잠금을 거치지 않는 경로(수동 SQL 등)에 대한 방어로 남긴다.
                db.ChangeTracker.Clear();
                return TypedResults.Ok(ToDto(await db.Attachments.AsNoTracking().SingleAsync(a => a.Sha256 == stored.Sha256, ct)));
            }
            loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation(
                "첨부 업로드. AttachmentId={AttachmentId} Sha256={Sha256} SizeBytes={SizeBytes}", attachment.Id, attachment.Sha256, attachment.SizeBytes);
            var dto = ToDto(attachment);
            return TypedResults.Created(dto.Url, dto);
        }
    }

    /// <summary>첨부의 DB 행과 저장된 파일을 함께 지운다.</summary>
    /// <param name="id">지울 첨부의 Id.</param>
    /// <param name="db">삭제에 쓸 DbContext.</param>
    /// <param name="store">파일 삭제를 수행하는 저장소.</param>
    /// <param name="loggers">감사 로그 기록용 로거 팩토리.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>삭제 성공 204, 없거나 이미 지워졌으면 404, 같은 내용의 잠금 대기가 10초를 넘으면 503(Retry-After).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다. <see cref="FileSystemAttachmentStore.TryDelete"/>의 동기 파일 삭제가 이 스레드를 짧게 막는다.</description></item>
    /// <item><description><b>Memory Policy:</b> 행 조회는 <c>Sha256</c>·<c>StoragePath</c> 두 필드만 프로젝션한다(전체 엔티티를 추적하지 않는다).</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 같은 내용(sha256)의 <see cref="AttachmentLock"/> 세션 잠금 안에서 행 삭제(<c>ExecuteDeleteAsync</c>, 자동 커밋)와 파일 삭제를 함께 수행해
    /// 업로드·청소 잡과 직렬화한다 — 잠금 안에서 삭제 대상 행이 이미 없으면(다른 탭이 먼저 지움) 404. 행을 먼저 지운다: 파일 삭제가 실패해도 남는 것은 참조 없는 파일뿐이고
    /// (청소 잡이 치운다), 반대 순서는 깨진 링크를 만든다. 잠금 대기가 10초를 넘으면 SqlState 55P03 → <c>OverloadExceptionHandler</c>가 503으로 바꾼다. Non-blocking: DB 호출은 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, FileSystemAttachmentStore store, ILoggerFactory loggers, CancellationToken ct)
    {
        var row = await db.Attachments.AsNoTracking().Where(a => a.Id == id).Select(a => new { a.Sha256, a.StoragePath }).SingleOrDefaultAsync(ct);
        if (row is null) return TypedResults.NotFound();

        var logger = loggers.CreateLogger("PortfolioBlog.Api.Audit");
        await using (await AttachmentLock.HoldAsync(db, row.Sha256, ct))
        {
            // ExecuteDeleteAsync: 자동 커밋되는 DELETE 한 문장. Sha256이 UNIQUE라 이 행이 그 파일의 유일한 참조다.
            if (await db.Attachments.Where(a => a.Id == id).ExecuteDeleteAsync(ct) == 0) return TypedResults.NotFound(); // 잠금을 기다리는 사이 다른 탭이 지웠다
            // 행을 먼저 지운다: 파일 삭제가 실패해도 남는 것은 참조 없는 파일뿐이고(청소 잡이 치운다), 반대 순서는 깨진 링크를 만든다.
            if (!store.TryDelete(row.StoragePath)) logger.LogWarning("첨부 파일 삭제 실패(고아 파일). AttachmentId={AttachmentId} Sha256={Sha256}", id, row.Sha256);
        }
        logger.LogInformation("첨부 삭제. AttachmentId={AttachmentId} Sha256={Sha256}", id, row.Sha256);
        return TypedResults.NoContent();
    }

    /// <summary>업로드된 파일 이름을 표시용으로 정리한다: 경로 조각·제어문자(NUL 포함)·홀로 남은 UTF-16 서러게이트 제거,
    /// 확장자는 시그니처 기준으로 교체, 길이 제한(코드 포인트 경계에서 자른다).</summary>
    /// <param name="uploaded">클라이언트가 보낸 원본 파일 이름(신뢰하지 않음).</param>
    /// <param name="kind">시그니처로 판정한 실제 형식(확장자의 출처).</param>
    /// <returns>경로 조각·제어문자·홀로 남은 서러게이트가 제거되고 확장자가 시그니처 기준으로 교체된 표시용 이름. 저장 경로에는 쓰이지 않는다.
    /// 반환값은 어떤 UTF-16 코드 단위에 대해서도 홀로 남은 서러게이트를 포함하지 않는다(<see cref="RemoveUnpairedSurrogates"/> 참조) —
    /// 그런 문자열을 그대로 DB에 쓰면 Npgsql의 UTF-8 인코더가 예외 폴백으로 <see cref="System.Text.EncoderFallbackException"/>을 던져 500이 된다(실측).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> 중간 문자열을 여러 개 할당한다(입력이 파일 이름 하나뿐이라 크기가 작다).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    internal static string DisplayName(string? uploaded, ImageKind kind)
    {
        var name = (uploaded ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        // ".." 제거보다 먼저 한다: 홀로 남은 서러게이트를 나중에 지우면 그 자리 양옆의 마침표가 새로 ".."을 만들 수 있다
        // (예: "a." + 홀로 있는 서러게이트 + ".b" → 서러게이트를 나중에 지우면 "a..b"가 된다).
        name = RemoveUnpairedSurrogates(name);
        name = name.Replace("..", string.Empty, StringComparison.Ordinal).Trim().Trim('.');
        var dot = name.LastIndexOf('.');
        // 아직 다듬지 않은 stem이다 — 끝의 마침표·공백은 아래 TrimTrailingDotsAndWhitespace가 자르기 이후(있다면)까지 포함해 한 번에 처리한다.
        // 슬라이스 직후에 곧바로 .Trim()하면 "a. .png" 같은 입력에서 마침표 앞 공백만 지워지고 그 뒤에 드러나는 마침표는 거르지 못해
        // stem이 "a."로 끝난 채 확장자와 합쳐져 ".."이 생긴다(자르기 없이도 재현됨).
        var stem = dot > 0 ? name[..dot] : name;
        var extension = "." + ImageSignature.Extension(kind);
        var max = AppDbContext.FileNameMax - extension.Length;
        if (stem.Length > max)
        {
            stem = stem[..max];
            // UTF-16 코드 단위 기준으로 자르므로 서러게이트 쌍 한가운데를 자를 수 있다 — 마지막 문자가 상위 서러게이트로 남으면
            // 짝(하위 서러게이트)이 잘려 나간 것이므로 함께 버려 코드 포인트 경계에서 자른 것으로 만든다.
            if (stem.Length > 0 && char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        }
        // 잘리지 않은 경로(위 dot > 0 슬라이스)와 잘린 경로(위 자르기) 둘 다, 끝에 마침표·공백이 남으면 확장자와 합쳐질 때 ".."을
        // 만들 수 있다 — 한 번의 TrimEnd('.')·TrimEnd()만으로는 "마침표를 지우면 그 앞의 공백이 드러나고, 그 공백을 지우면 다시
        // 마침표가 드러나는" 꼬리를 끝까지 걷어내지 못하므로 더 지울 게 없을 때까지 반복한다.
        stem = TrimTrailingDotsAndWhitespace(stem);
        if (stem.Length == 0) stem = "image";
        return stem + extension;
    }

    /// <summary>문자열 끝에서 마침표와 공백을 번갈아 가며 더는 지울 게 없을 때까지 반복해서 걷어낸다.</summary>
    /// <param name="value">다듬을 문자열.</param>
    /// <returns>끝에 마침표도 공백도 남지 않은 문자열(원래 전부 마침표·공백이었으면 빈 문자열).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> 반복마다 <see cref="string.TrimEnd()"/>가 부분 문자열을 새로 할당한다. 파일 이름 하나 분량이라 반복 횟수·크기 모두 작다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    private static string TrimTrailingDotsAndWhitespace(string value)
    {
        int previousLength;
        do
        {
            previousLength = value.Length;
            value = value.TrimEnd().TrimEnd('.');
        } while (value.Length != previousLength);
        return value;
    }

    /// <summary>문자열에서 홀로 남은(짝 없는) UTF-16 서러게이트를 제거한다: 상위 서러게이트 뒤에 하위 서러게이트가 없거나,
    /// 하위 서러게이트 앞에 상위 서러게이트가 없으면 그 문자는 완전한 코드 포인트를 이루지 못하므로 버린다.</summary>
    /// <param name="value">정리할 문자열.</param>
    /// <returns>온전한 서러게이트 쌍만 남긴(또는 애초에 서러게이트가 없던) 문자열.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> 서러게이트가 하나도 없으면(대부분의 파일 이름) 원본을 그대로 반환해 할당이 없다.
    /// 하나라도 있으면 <see cref="StringBuilder"/> 1개를 새로 만든다 — 파일 이름 하나 분량이라 크기가 작다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    private static string RemoveUnpairedSurrogates(string value)
    {
        var hasSurrogate = false;
        foreach (var c in value) { if (char.IsSurrogate(c)) { hasSurrogate = true; break; } }
        if (!hasSurrogate) return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[i + 1]);
                i++; // 짝을 함께 썼으니 하위 서러게이트 자리는 건너뛴다
            }
            else if (!char.IsSurrogate(c))
            {
                builder.Append(c);
            }
            // else: 홀로 남은 상위/하위 서러게이트 — 버린다
        }
        return builder.ToString();
    }

    /// <summary>413(첨부 크기 한도 초과) <see cref="ProblemDetails"/> 응답을 만든다.</summary>
    /// <returns>상태 코드 413으로 설정된 결과.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 매개변수 없이 새 결과 객체를 만든다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 결과 객체 1개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    private static IResult TooLarge() =>
        TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "첨부가 너무 큽니다", detail: $"이미지는 {AttachmentOptions.MaxBytes / 1_048_576}MB 이하여야 합니다.");
}
