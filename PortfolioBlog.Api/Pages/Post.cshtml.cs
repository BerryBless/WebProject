using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Pages;

/// <summary>글 상세. 본문은 캐시 미스일 때만 DB에서 읽고, 렌더는 버전당 한 번이다.</summary>
/// <param name="db">메타·본문 조회에 쓸 공개 전용 컨텍스트.</param>
/// <param name="cache">글 버전별 렌더 결과 캐시.</param>
/// <param name="site">사이트 설정(제목·소개·origin).</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. Razor Pages가 요청마다 새 인스턴스를 만들어 스레드 간 공유가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Meta"/>(태그·시리즈 이웃 포함) + 캐시 미스일 때만 본문 문자열(최대 200KB)과 렌더 결과 1개.</description></item>
/// <item><description><b>Blocking:</b> <see cref="OnGetAsync"/>가 비동기 Non-blocking으로 DB·캐시·(미스 시) 렌더 게이트를 오간다.</description></item>
/// </list>
/// </remarks>
public sealed class PostModel(PublicDbContext db, RenderedPostCache cache, IOptions<SiteOptions> site) : PublicPageModel(site)
{
    /// <summary>이번 요청의 글 메타데이터(본문 제외).</summary>
    public PublicPostMeta Meta { get; private set; } = null!;

    /// <summary>정제된 본문 HTML. 두 경우에 <c>null</c>: (1) 렌더러가 거부한 글(중첩 한도 초과 — 렌더러 규칙이 저장 이후에 엄격해진 경우),
    /// (2) 메타데이터를 읽은 직후 다른 요청이 이 글을 삭제해 본문 조회가 빈 결과를 돌려준 경우(다음 요청은 메타데이터 조회부터 404가 된다).</summary>
    public string? BodyHtml { get; private set; }

    /// <summary>slug로 글을 찾아 메타데이터를 채우고, 본문을 캐시에서 찾거나(미스면) 렌더링한다.</summary>
    /// <param name="slug">요청 경로의 글 slug.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>정상이면 이 페이지, slug 형식이 틀리거나 글이 없으면 404.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 이 요청 인스턴스 안에서만 호출된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="PublicQueries.GetPostAsync"/> 문서 참조 + 캐시 미스일 때만 <see cref="RenderAsync"/>의 할당.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 메타데이터 조회 1~2회 + (캐시 미스일 때만) 본문 조회·렌더를 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        // 형식 밖 slug(대문자·밑줄·NUL·100자 초과)는 DB에 가지 않는다.
        if (!SlugRules.IsValid(slug)) return NotFound();
        var meta = await PublicQueries.GetPostAsync(db, slug, ct);
        if (meta is null) return NotFound();

        var rendered = cache.TryGet(meta.Id, meta.Version, out var hit) ? hit : await RenderAsync(meta.Id, ct);
        BodyHtml = rendered?.Html;
        Meta = meta;
        SetHead(meta.Title, meta.Summary, PublicUrls.Post(meta.Slug), "article", rendered?.FirstImageUrl);
        return Page();
    }

    /// <summary>캐시 미스 본문을 조회해 렌더링한다. 렌더러가 거부하면 <see langword="null"/>을 돌려 본문 없는 쪽으로 대체한다.</summary>
    /// <param name="id">글의 내부 식별자.</param>
    /// <param name="ct">요청 취소 토큰(공유 렌더 자체는 취소되지 않는다 — <see cref="RenderedPostCache.GetOrRenderAsync"/> 참조).</param>
    /// <returns>렌더 결과, 또는 본문이 이미 사라졌거나 렌더러가 거부했으면 <see langword="null"/>.</returns>
    private async Task<RenderedMarkdown?> RenderAsync(Guid id, CancellationToken ct)
    {
        var content = await PublicQueries.GetContentAsync(db, id, ct);
        if (content is null) return null; // 메타데이터를 읽은 직후 삭제됐다 — 본문 없는 쪽으로 보여 준다(다음 요청은 404)
        try
        {
            return await cache.GetOrRenderAsync(id, content.Version, content.Markdown, ct);
        }
        catch (MarkdownTooComplexException)
        {
            return null;
        }
    }
}
