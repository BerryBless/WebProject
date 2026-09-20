namespace PortfolioBlog.Api.Domain;

/// <summary>글에 붙는 태그.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. <see cref="Infrastructure.Data.AppDbContext"/> 스코프 안에서 단일 스레드로만 사용한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 인스턴스당 힙 할당 1개 + <see cref="PostTags"/> 컬렉션.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. 순수 데이터 컨테이너이며 자체 I/O가 없다.</description></item>
/// </list>
/// </remarks>
public sealed class Tag
{
    /// <summary>기본 키. 시간 정렬 가능한 UUIDv7로 생성한다.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>표시용 이름(원문 대소문자 유지, 1~50자, '/' 금지).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>트림 + 연속 공백 1개 + NFC + 소문자. 유일 인덱스 대상이자 공개 URL 키.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>이 태그가 붙은 글 연결 엔티티 목록(다대다 조인).</summary>
    public List<PostTag> PostTags { get; } = new();
}
