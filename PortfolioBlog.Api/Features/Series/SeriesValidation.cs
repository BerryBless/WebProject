using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Features.Series;

/// <summary><see cref="UpsertSeriesRequest"/>의 형식 검증(DB 조회 없음). 의미 검증(slug 중복·slug 불변)은 엔드포인트가 한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스이며 공유 가변 상태가 없어 여러 요청이 동시에 호출해도 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 오류가 있을 때만 <see cref="ValidationErrors"/> 내부에 메시지 문자열을 할당한다. <see cref="ValidationErrors"/> 인스턴스 1개는 항상 할당된다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
/// </list>
/// </remarks>
public static class SeriesValidation
{
    /// <summary><paramref name="req"/>의 형식(slug·제목·설명)을 검사한다.</summary>
    /// <param name="req">검사할 생성·수정 요청.</param>
    /// <returns>검사 과정에서 누적된 <see cref="ValidationErrors"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 매개변수만 읽고 새 <see cref="ValidationErrors"/>를 반환한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="ValidationErrors"/> 1개 + 오류가 있을 때만 메시지 문자열.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public static ValidationErrors Validate(UpsertSeriesRequest req)
    {
        var errors = new ValidationErrors();

        // NUL(U+0000)은 PostgreSQL text 컬럼에 저장할 수 없다(JSON은 유니코드 이스케이프로 NUL을 실어 나를 수 있어 여기서 걸러야 DB에서 500이 되지 않는다).
        if (string.IsNullOrEmpty(req.Slug)) errors.Add("slug", "slug는 필수입니다.");
        else if (TextRules.ContainsNul(req.Slug)) errors.Add("slug", TextRules.NulMessage);
        else if (!SlugRules.IsValid(req.Slug)) errors.Add("slug", $"slug는 소문자·숫자·하이픈만 쓰고 {AppDbContext.SlugMax}자 이하여야 합니다.");

        if (string.IsNullOrWhiteSpace(req.Title)) errors.Add("title", "제목은 비울 수 없습니다.");
        else if (TextRules.ContainsNul(req.Title)) errors.Add("title", TextRules.NulMessage);
        else if (req.Title.Trim().Length > AppDbContext.TitleMax) errors.Add("title", $"제목은 {AppDbContext.TitleMax}자 이하여야 합니다.");

        if (TextRules.ContainsNul(req.Description)) errors.Add("description", TextRules.NulMessage);
        else if ((req.Description?.Trim().Length ?? 0) > AppDbContext.SeriesDescriptionMax) errors.Add("description", $"설명은 {AppDbContext.SeriesDescriptionMax}자 이하여야 합니다.");

        return errors;
    }
}
