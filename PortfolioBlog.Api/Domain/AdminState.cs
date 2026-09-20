namespace PortfolioBlog.Api.Domain;

/// <summary>단일 행(Id=1) 관리 상태. <see cref="SessionEpoch"/>를 올리면 그 전에 발급된 모든 세션 쿠키가 무효가 된다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. <see cref="Infrastructure.Data.AppDbContext"/> 스코프 안에서 단일 스레드로만 사용한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 인스턴스당 힙 할당 1개(필드 2개, 추가 컬렉션 없음).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. 순수 데이터 컨테이너이며 자체 I/O가 없다.</description></item>
/// </list>
/// </remarks>
public sealed class AdminState
{
    /// <summary>DB CHECK 제약으로 강제되는 유일 허용 Id 값.</summary>
    public const int SingletonId = 1;

    /// <summary>항상 <see cref="SingletonId"/>(1) 값만 허용되는 기본 키(DB CHECK).</summary>
    public int Id { get; set; } = SingletonId;

    /// <summary>세션 무효화 세대 번호. 증가시키면 그 이전에 발급된 모든 세션 쿠키가 무효화된다.</summary>
    public int SessionEpoch { get; set; } = 1;
}
