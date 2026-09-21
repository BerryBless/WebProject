namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>설정 섹션 <c>Public</c>. 인증 없는 공개 표면의 자원 예산(스펙 3.7).</summary>
public sealed class PublicOptions
{
    /// <summary>설정 섹션 이름.</summary>
    public const string SectionName = "Public";

    /// <summary>공개 페이지·피드·sitemap의 IP별 분당 한도. 검색 요청도 여기에 함께 계산된다.</summary>
    public int PagePerIpPerMinute { get; set; } = 120;

    /// <summary>첨부 GET·<c>/health</c>·<c>robots.txt</c>·<c>highlight.css</c>의 IP별 분당 한도. 이미지가 많은 글 한 번이 페이지 한도를 다 쓰지 않게 분리했다.</summary>
    public int AssetPerIpPerMinute { get; set; } = 600;

    /// <summary><c>/search</c>의 IP별 분당 한도.</summary>
    public int SearchPerIpPerMinute { get; set; } = 20;

    /// <summary>동시에 실행할 수 있는 검색 수(전역). <c>ILIKE</c> 전체 스캔이 DB 연결을 독점하지 못하게 묶는다.</summary>
    public int SearchConcurrency { get; set; } = 4;

    /// <summary>공개 조회 연결의 <c>statement_timeout</c>(밀리초). 100~60000.</summary>
    public int StatementTimeoutMs { get; set; } = 3000;
}
