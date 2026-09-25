using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary><see cref="DateTimeOffset"/>(오프셋 0만)을 MySQL <c>DATETIME(6)</c>용 UTC <see cref="DateTime"/>으로 바꾼다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. EF가 모델당 하나를 공유하며 변환 식은 무상태다.</description></item>
/// <item><description><b>Memory Allocation:</b> 값 형식 변환이라 추가 힙 할당 없음(예외 경로 제외).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// DATETIME에는 오프셋이 없다. 앱은 <see cref="DbClock.UtcNow"/>만 쓰므로 0이 아닌 오프셋은 버그다. 조용히 UTC로 바꾸지 않고 예외로 드러낸다.
/// </remarks>
public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTime>(
    value => ToUtc(value),
    value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
{
    private static DateTime ToUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero ? value.UtcDateTime : throw new InvalidOperationException("DB에는 UTC(오프셋 0) 시각만 저장한다.");
}
