namespace PortfolioBlog.Api.Domain;

/// <summary>연재 묶음. 글은 0~1개 시리즈에 속한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. <see cref="Infrastructure.Data.AppDbContext"/> 스코프 안에서 단일 스레드로만 사용한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 인스턴스당 힙 할당 1개 + <see cref="Posts"/> 컬렉션.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. 순수 데이터 컨테이너이며 자체 I/O가 없다.</description></item>
/// </list>
/// </remarks>
public sealed class Series
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<Post> Posts { get; } = new();
}
