using System.Text;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Features.Preview;

/// <summary>에디터 미리보기. 공개 페이지와 <b>같은</b> <see cref="MarkdownRenderer"/>를 써서 "미리보기와 발행 결과가 다르다"를 없앤다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 무상태 정적 핸들러. 렌더러·게이트는 Thread-safe 싱글턴.</description></item>
/// <item><description><b>Memory Allocation:</b> 요청 본문(≤200KB) + 렌더링 중간 산출물.</description></item>
/// <item><description><b>Blocking:</b> 렌더링은 <see cref="RenderGate"/>가 내준 슬롯 안에서 요청 스레드가 동기 실행하는 취소 불가 CPU 작업이다(최대 수백 ms가 아니다). 코드 강조는
/// <see cref="HighlightingCodeBlockRenderer.MaxHighlightMilliseconds"/> + 정규식 매치 타임아웃으로 렌더 1회당 대략 2,250ms를 넘지 않지만,
/// Markdig 자체의 인라인·블록 파서는 적대적인 ~200KB 입력에서 초선형이라 측정상 최대 약 8.5초까지 걸린다(<see cref="MarkdownRenderer"/> 문서 참조).
/// 그래서 분당 한도(<see cref="RateLimitPolicy.Preview"/> 창)<b>와</b> 동시 실행 제한(같은 정책의 동시성 파티션)이 둘 다 필요하다 —
/// 분당 한도만으로는 그 1분 동안 여러 요청이 동시에 CPU를 물고 늘어지는 스레드 풀 고갈을 막지 못한다. 프런트의 디바운스는 사용자 경험일 뿐,
/// 이 방어의 일부가 아니다(클라이언트가 아무 제약 없이 직접 호출해도 서버가 막아야 한다). 미리보기 전용 동시 실행 제한(2) 위에 전역 <see cref="RenderGate"/>가 한 겹 더 있다
/// (미리보기·글 저장·공개 페이지가 프로세스 전체에서 같은 슬롯 수를 나눠 쓴다).</description></item>
/// </list>
/// 본문은 로그에 남기지 않는다.
/// </remarks>
public static class PreviewEndpoints
{
    /// <summary><c>/preview</c> 엔드포인트를 등록한다.</summary>
    /// <param name="api"><c>/api</c> 루트 그룹.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출되어 라우트를 등록한다. 등록되는 엔드포인트 델리게이트는 요청마다 병렬로 안전하게 호출된다(공유 가변 상태 없음, 의존성은 요청 스코프 또는 싱글턴으로 주입).</description></item>
    /// <item><description><b>Memory Allocation:</b> 라우트 등록에 따른 시작 시 1회성 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static void MapPreviewEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/preview", Render)
            .WithMetadata(new RateLimitMetadata(RateLimitPolicy.Preview))
            .WithName("PreviewMarkdown");
    }

    /// <summary>요청 본문의 마크다운을 렌더링해 돌려준다. 공개 페이지와 같은 렌더러를 쓰므로 미리보기와 발행 결과가 갈리지 않는다.</summary>
    /// <param name="request">미리보기 요청 본문.</param>
    /// <param name="gate">공개 페이지·글 저장과 공유하는 전역 렌더 게이트.</param>
    /// <param name="ct">요청 취소 토큰(슬롯 대기만 취소한다).</param>
    /// <returns>렌더링한 HTML을 담은 200, 검증 실패이거나 구조가 너무 깊게 중첩됐으면 400, 게이트가 가득 차 대기 상한을 넘었으면 503.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> <see cref="RenderGate.RenderAsync"/>가 만드는 <see cref="RenderedMarkdown"/>의 <c>Html</c> 문자열 1개를 응답 DTO에 실어 그대로 반환한다(서버에 보관하지 않는다).</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. Non-blocking: 슬롯 대기는 <c>await</c>한다. 슬롯을 얻은 뒤의 렌더 자체는 동기 CPU 작업이라(<see cref="RenderGate"/> 문서 참조) 이 메서드도 그동안 요청 스레드를 점유한다(취소 불가). 분당 한도·동시 실행 제한이 상위(<see cref="RateLimitingExtensions"/>)에서, 전역 게이트가 프로세스 전체에서 스레드 풀 고갈을 막는다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> Render(PreviewRequest request, RenderGate gate, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (request.Markdown is null) errors.Add("markdown", "markdown은 필수입니다(빈 문자열은 허용).");
        else if (TextRules.ContainsNul(request.Markdown)) errors.Add("markdown", TextRules.NulMessage);
        else if (Encoding.UTF8.GetByteCount(request.Markdown) > MarkdownRenderer.MaxInputBytes)
            errors.Add("markdown", $"본문은 UTF-8 기준 {MarkdownRenderer.MaxInputBytes / 1024}KB 이하여야 합니다.");
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        // Features는 서로 참조하지 않으므로(전역 제약) PostEndpoints.RenderOrAddErrorAsync를 재사용하지 않고 같은 확인을 여기서 다시 한다:
        // 저장 없이 렌더링만 하는 미리보기도 저장 경로와 똑같이 중첩 한도 초과를 500이 아니라 400으로 돌려줘야 한다.
        try
        {
            return TypedResults.Ok(new PreviewResponse((await gate.RenderAsync(request.Markdown!, ct)).Html));
        }
        catch (MarkdownTooComplexException)
        {
            return TypedResults.ValidationProblem(new ValidationErrors()
                .Add("markdown", "마크다운 구조가 너무 깊게 중첩됐습니다. 중첩을 줄여주세요.")
                .ToDictionary());
        }
    }
}
