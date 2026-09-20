using System.Text;
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Contracts;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>태그 이름 목록을 정규화·중복 제거하고, 없는 태그는 <c>ON CONFLICT DO NOTHING</c>으로 만들어 Id 목록을 돌려준다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 전달된 DbContext의 스코프(와 열려 있는 트랜잭션) 안에서만 호출한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 태그 수(최대 20)만큼의 문자열·리스트.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 새 태그마다 INSERT 1회 + 조회 1회를 await 한다.</description></item>
/// </list>
/// "조회 → 없으면 EF로 Add" 방식은 두 요청이 같은 새 태그를 동시에 만들 때 한쪽이 유니크 위반으로 실패한다.
/// <c>ON CONFLICT (NormalizedName) DO NOTHING</c>은 그 경쟁을 DB가 흡수하므로 정상적인 동시 저장을 작성자에게 409로 돌려주지 않는다.
/// </remarks>
public static class TagResolver
{
    /// <summary>글 하나에 연결할 수 있는 태그 최대 개수.</summary>
    public const int MaxTagsPerPost = 20;

    /// <summary>표시용: 트림 + 연속 공백 1개 + NFC. 대소문자는 유지한다.</summary>
    /// <param name="raw">정리 전 원본 태그 문자열.</param>
    /// <returns>트림·공백 축소·NFC 정규화를 거친 표시용 문자열.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 순수 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="string.Split(char[]?, StringSplitOptions)"/>의 배열 1개와 <see cref="string.Join(char, string?[])"/>·<see cref="string.Normalize(NormalizationForm)"/>의 결과 문자열.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public static string Display(string raw) =>
        string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Normalize(NormalizationForm.FormC);

    /// <summary>유일성 키: <see cref="Display"/> + 불변 문화권 소문자.</summary>
    /// <param name="raw">정규화 전 원본 태그 문자열.</param>
    /// <returns>유일성 비교에 쓰는 정규화된 소문자 키.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 순수 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="Display"/> 호출분 + <see cref="string.ToLowerInvariant"/> 결과 문자열 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public static string Normalize(string raw) => Display(raw).ToLowerInvariant();

    /// <summary>태그 이름 목록의 형식(슬래시·길이·개수)을 검사해 <paramref name="errors"/>에 누적한다. DB 조회는 하지 않는다.</summary>
    /// <param name="names">검사할 원본 태그 이름 목록(<c>null</c>이면 검사하지 않음).</param>
    /// <param name="errors">오류를 누적할 대상.</param>
    /// <param name="field">오류를 붙일 요청 필드 이름.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 전달받은 <paramref name="errors"/> 인스턴스를 변경하며, 그 인스턴스는 단일 요청 스코프에서만 공유된다고 가정한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 중복 제거용 <see cref="HashSet{T}"/> 1개(최대 <see cref="MaxTagsPerPost"/> 이상 크기 가능, 초과분은 오류만 남고 버려짐)와 오류가 있을 때만 메시지 문자열.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static void Validate(IEnumerable<string>? names, ValidationErrors errors, string field)
    {
        if (names is null) return;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var display = Display(raw);
            if (display.Length > AppDbContext.TagMax) errors.Add(field, $"태그는 {AppDbContext.TagMax}자 이하여야 합니다: {display[..20]}…");
            else if (display.Contains('/')) errors.Add(field, $"태그에 '/'를 쓸 수 없습니다: {display}");
            else distinct.Add(display.ToLowerInvariant());
        }
        if (distinct.Count > MaxTagsPerPost) errors.Add(field, $"태그는 글당 {MaxTagsPerPost}개까지입니다.");
    }

    /// <summary>태그 이름 목록을 정규화해 존재하는 태그는 재사용하고, 없는 태그는 동시 생성 경합에 안전하게 새로 만들어 Id 목록을 돌려준다.</summary>
    /// <param name="db">태그를 조회·생성할 DbContext(열려 있는 트랜잭션 안에서 호출해야 한다).</param>
    /// <param name="names">연결할 태그 이름 목록(<c>null</c>이면 빈 목록 반환).</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>정규화된 각 태그 이름에 대응하는 <see cref="Domain.Tag.Id"/> 목록(중복 없음, 순서 불특정).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 전달된 <paramref name="db"/> 스코프 안에서만 단일 스레드로 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 요청 태그 수(최대 <see cref="MaxTagsPerPost"/>)에 비례하는 사전·리스트·문자열. 결과 <see cref="List{T}"/>는 호출자가 소유한다.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 새 태그마다 INSERT 1회를 <c>await</c>하고, 마지막에 조회 1~2회를 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<List<Guid>> ResolveIdsAsync(AppDbContext db, IEnumerable<string>? names, CancellationToken ct)
    {
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal); // normalized → display(먼저 나온 표기 우선)
        foreach (var raw in names ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var display = Display(raw);
            wanted.TryAdd(display.ToLowerInvariant(), display);
        }
        if (wanted.Count == 0) return [];

        var keys = wanted.Keys.ToArray();
        var existing = await db.Tags.AsNoTracking().Where(t => keys.Contains(t.NormalizedName)).Select(t => t.NormalizedName).ToListAsync(ct);
        foreach (var (normalized, display) in wanted)
        {
            if (existing.Contains(normalized)) continue;
            var id = Guid.CreateVersion7();
            // 보간 값은 전부 매개변수로 전달된다(ExecuteSqlInterpolated). 테이블·컬럼 이름만 리터럴이다.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO "Tags" ("Id", "Name", "NormalizedName") VALUES ({id}, {display}, {normalized}) ON CONFLICT ("NormalizedName") DO NOTHING""", ct);
        }
        return await db.Tags.AsNoTracking().Where(t => keys.Contains(t.NormalizedName)).Select(t => t.Id).ToListAsync(ct);
    }
}
