using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>공개 조회 전용 DB 롤(<c>ConnectionStrings:Public</c>의 사용자)에게 공개 페이지가 읽는 테이블의 <c>SELECT</c>만 부여한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다. <see cref="Apply"/>는 시작 스레드에서 마이그레이션 직후 1회만 호출된다(단일 인스턴스 배포 — 스펙 3.10).</description></item>
/// <item><description><b>Memory Allocation:</b> 시작 시 SQL 문자열 몇 개(테이블 수 + 3)만 할당한다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="Apply"/>는 동기 DB 왕복(트랜잭션 1개)이다. 요청 처리 경로에서 부르지 않는다.</description></item>
/// </list>
/// 테이블 목록을 EF 모델에서 뽑지 않는 이유: <see cref="PublicDbContext"/>는 <see cref="AppDbContext"/>를 상속해 모델에
/// <c>AdminState</c>(세션 폐기 카운터)와 마이그레이션 이력까지 들어 있다. 공개 롤이 읽어도 되는 것은 아래 목록뿐이다.
/// </remarks>
public static partial class PublicRoleGrants
{
    /// <summary>공개 롤이 읽을 수 있는 테이블. 공개 페이지·피드·첨부 GET이 실제로 조회하는 것만 둔다.</summary>
    public static readonly IReadOnlyList<string> ReadableTables = ["Posts", "Series", "Tags", "PostTags", "Attachments"];

    /// <summary>따옴표 없이 쓸 수 있는 소문자 식별자만 롤 이름으로 받는다. 롤 이름은 SQL 매개변수가 될 수 없어 문장에 직접 들어가기 때문이다.</summary>
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleNamePattern();

    /// <summary>공개 연결 문자열에서 롤 이름을 꺼내 검증한다.</summary>
    /// <param name="publicConnectionString"><c>ConnectionStrings:Public</c> 값.</param>
    /// <returns>검증된 롤 이름.</returns>
    /// <exception cref="InvalidOperationException">사용자 이름이 없거나 <c>^[a-z_][a-z0-9_]{0,62}$</c>에 맞지 않을 때. 메시지에 연결 문자열 값은 넣지 않는다.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 연결 문자열 파서 1개와 결과 문자열.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음 — <c>StartupValidation</c>에서도 부를 수 있다.</description></item>
    /// </list>
    /// </remarks>
    public static string RoleOf(string publicConnectionString)
    {
        var role = new NpgsqlConnectionStringBuilder(publicConnectionString).Username;
        return !string.IsNullOrEmpty(role) && RoleNamePattern().IsMatch(role)
            ? role
            : throw new InvalidOperationException("ConnectionStrings:Public 의 Username 은 소문자·숫자·밑줄로 된 롤 이름이어야 합니다.");
    }

    /// <summary>롤에 적용할 문장을 순서대로 만든다: 전부 회수 → 스키마 사용 → 허용 테이블 <c>SELECT</c>.</summary>
    /// <param name="role"><see cref="RoleOf"/>가 검증한 롤 이름.</param>
    /// <returns>한 트랜잭션에서 순서대로 실행할 SQL 문장들.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 문장 수만큼의 문자열과 배열 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// 먼저 전부 회수하는 이유: 목록에서 빠진 테이블의 옛 권한이나 손으로 준 권한이 남지 않게 한다.
    /// </remarks>
    public static IReadOnlyList<string> BuildStatements(string role)
    {
        if (!RoleNamePattern().IsMatch(role)) throw new ArgumentException("검증되지 않은 롤 이름", nameof(role));
        var statements = new List<string>
        {
            $"REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {role}",
            $"GRANT USAGE ON SCHEMA public TO {role}",
        };
        foreach (var table in ReadableTables)
        {
            statements.Add($"GRANT SELECT ON \"{table.Replace("\"", "\"\"")}\" TO {role}");
        }
        return statements;
    }

    /// <summary>관리 연결(테이블 소유자)로 권한을 다시 맞춘다. 마이그레이션 직후에 부른다 — 새 테이블이 생긴 뒤여야 한다.</summary>
    /// <param name="db">테이블 소유자 롤로 접속하는 관리 컨텍스트.</param>
    /// <param name="publicConnectionString"><c>ConnectionStrings:Public</c> 값.</param>
    /// <exception cref="InvalidOperationException">공개 롤이 관리 롤과 같을 때(권한을 회수하면 앱이 자기 테이블을 못 읽는다), 또는 롤 이름이 잘못됐을 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe(<paramref name="db"/>가 단일 스레드 전용). 시작 시 1회 호출.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="BuildStatements"/>의 문자열들.</description></item>
    /// <item><description><b>Blocking:</b> 동기 블로킹. 트랜잭션 하나에서 문장 몇 개를 실행한다 — 중간에 실패하면 권한은 이전 상태로 남는다.</description></item>
    /// </list>
    /// </remarks>
    public static void Apply(AppDbContext db, string publicConnectionString)
    {
        var role = RoleOf(publicConnectionString);
        var owner = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()).Username;
        if (string.Equals(role, owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ConnectionStrings:Public 의 Username 이 ConnectionStrings:Default 와 같습니다 — 공개 조회는 별도의 읽기 전용 롤이어야 합니다.");
        }
        using var transaction = db.Database.BeginTransaction();
        foreach (var statement in BuildStatements(role))
        {
            // 문장은 검증된 식별자와 상수 목록으로만 조립된다(사용자 입력 없음). GRANT의 대상 식별자는 SQL 매개변수가 될 수 없다.
#pragma warning disable EF1002
            db.Database.ExecuteSqlRaw(statement);
#pragma warning restore EF1002
        }
        transaction.Commit();
    }
}
