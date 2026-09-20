namespace PortfolioBlog.Api.Domain;

/// <summary>글-태그 다대다 연결 엔티티.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. <see cref="Infrastructure.Data.AppDbContext"/> 스코프 안에서 단일 스레드로만 사용한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 인스턴스당 힙 할당 1개(복합키, 추가 컬렉션 없음).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. 순수 데이터 컨테이너이며 자체 I/O가 없다.</description></item>
/// </list>
/// </remarks>
public sealed class PostTag
{
    public Guid PostId { get; set; }

    public Post Post { get; set; } = null!;

    public Guid TagId { get; set; }

    public Tag Tag { get; set; } = null!;
}
