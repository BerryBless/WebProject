using System.Text;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Features.Posts;

/// <summary><see cref="UpsertPostRequest"/>의 형식 검증(DB 조회 없음). 의미 검증(시리즈 존재·slug 불변·version)은 엔드포인트가 한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스이며 공유 가변 상태가 없어 여러 요청이 동시에 호출해도 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 오류가 있을 때만 <see cref="ValidationErrors"/> 내부에 메시지 문자열을 할당한다. <see cref="ValidationErrors"/> 인스턴스 1개는 항상 할당된다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음. 본문 크기는 글자 수가 아니라 UTF-8 바이트로 잰다(DB CHECK와 같은 단위).</description></item>
/// </list>
/// </remarks>
public static class PostValidation
{
    /// <summary><paramref name="req"/>의 형식(slug·제목·요약·본문·태그·시리즈 쌍)을 검사한다.</summary>
    /// <param name="req">검사할 생성·수정 요청.</param>
    /// <returns>검사 과정에서 누적된 <see cref="ValidationErrors"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 매개변수만 읽고 새 <see cref="ValidationErrors"/>를 반환한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="ValidationErrors"/> 1개 + 오류가 있을 때만 메시지 문자열. <see cref="Encoding.UTF8"/>의 <c>GetByteCount</c>는 버퍼를 할당하지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public static ValidationErrors Validate(UpsertPostRequest req)
    {
        var errors = new ValidationErrors();

        if (string.IsNullOrEmpty(req.Slug)) errors.Add("slug", "slug는 필수입니다.");
        else if (!SlugRules.IsValid(req.Slug)) errors.Add("slug", $"slug는 소문자·숫자·하이픈만 쓰고 {AppDbContext.SlugMax}자 이하여야 합니다(예: my-first-post).");

        if (string.IsNullOrWhiteSpace(req.Title)) errors.Add("title", "제목은 비울 수 없습니다.");
        else if (req.Title.Trim().Length > AppDbContext.TitleMax) errors.Add("title", $"제목은 {AppDbContext.TitleMax}자 이하여야 합니다.");

        if ((req.Summary?.Trim().Length ?? 0) > AppDbContext.SummaryMax) errors.Add("summary", $"요약은 {AppDbContext.SummaryMax}자 이하여야 합니다.");

        if (req.ContentMarkdown is null) errors.Add("contentMarkdown", "본문은 필수입니다(빈 문자열은 허용).");
        else if (Encoding.UTF8.GetByteCount(req.ContentMarkdown) > AppDbContext.ContentMaxBytes)
            errors.Add("contentMarkdown", $"본문은 UTF-8 기준 {AppDbContext.ContentMaxBytes / 1024}KB 이하여야 합니다.");

        TagResolver.Validate(req.TagNames, errors, "tagNames");

        if ((req.SeriesId is null) != (req.SeriesOrder is null)) errors.Add("seriesOrder", "seriesId와 seriesOrder는 함께 주거나 함께 생략해야 합니다.");
        else if (req.SeriesOrder is <= 0) errors.Add("seriesOrder", "seriesOrder는 1 이상이어야 합니다.");

        return errors;
    }
}
