using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>공개 조회 전용 DB 롤(<c>ConnectionStrings:Public</c>의 사용자)에게 공개 페이지가 읽는 테이블의 <c>SELECT</c>만 부여한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다. <see cref="Apply"/>는 시작 스레드에서 마이그레이션 직후 1회만 호출된다(단일 인스턴스 배포 — 스펙 3.10).</description></item>
/// <item><description><b>Memory Allocation:</b> 시작 시 관리 롤이 소유한 테이블 수에 비례하는 SQL 문자열을 할당한다(소유 테이블당 <c>REVOKE</c> 2개 + <c>GRANT USAGE</c> 1개 + 이미 존재하는 허용 테이블당 <c>GRANT SELECT</c> 1개).</description></item>
/// <item><description><b>Blocking:</b> <see cref="Apply"/>는 동기 DB 왕복(소유 테이블 조회 1회 + 트랜잭션 1개)이다. 요청 처리 경로에서 부르지 않는다.</description></item>
/// </list>
/// 테이블 목록을 EF 모델에서 뽑지 않는 이유: <see cref="PublicDbContext"/>는 <see cref="AppDbContext"/>를 상속해 모델에
/// <c>AdminState</c>(세션 폐기 카운터)와 마이그레이션 이력까지 들어 있다. 공개 롤이 읽어도 되는 것은 아래 목록뿐이다.
/// </remarks>
public static partial class PublicRoleGrants
{
    /// <summary>공개 롤이 읽을 수 있는 테이블. 공개 페이지·피드·첨부 GET이 실제로 조회하는 것만 둔다.</summary>
    public static readonly IReadOnlyList<string> ReadableTables = ["Posts", "Series", "Tags", "PostTags", "Attachments"];

    /// <summary>따옴표 없이 쓸 수 있는 소문자 식별자만 롤 이름으로 받는다. 롤 이름은 SQL 매개변수가 될 수 없어 문장에 직접 들어가기 때문이다.</summary>
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}\\z")]
    private static partial Regex RoleNamePattern();

    [GeneratedRegex("^GRANT (?<privs>.+?) ON (?<obj>\\S+) TO ")]
    private static partial Regex GrantLine();

    /// <summary>공개 사용자의 <c>SHOW GRANTS</c> 출력이 {USAGE ON *.*} ∪ {현재 DB 허용 테이블 SELECT}와 정확히 같은지 판정한다.</summary>
    /// <param name="showGrantsLines">공개 연결에서 실행한 <c>SHOW GRANTS</c>의 각 행.</param>
    /// <param name="database">현재 DB 이름.</param>
    /// <param name="readableTables">SELECT를 허용할 테이블 목록.</param>
    /// <returns>위반 설명 목록. 비어 있으면 통과.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> 기대 집합 HashSet 1개와 위반 목록. 기동 시 한 번만 호출된다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// 테이블 이름은 대소문자를 무시하고 비교한다(Windows MySQL은 소문자로 보고). 롤 부여 행(<c>ON</c> 없음)·<c>WITH GRANT OPTION</c>·다른 DB·전역 권한은 모두 위반이다.
    /// </remarks>
    public static IReadOnlyList<string> Violations(IEnumerable<string> showGrantsLines, string database, IReadOnlyList<string> readableTables)
    {
        var expected = readableTables.Select(t => $"`{database}`.`{t}`").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();
        foreach (var line in showGrantsLines)
        {
            var match = GrantLine().Match(line);
            if (!match.Success || line.Contains(" WITH GRANT OPTION", StringComparison.Ordinal)) { violations.Add(line); continue; }
            var privileges = match.Groups["privs"].Value;
            var target = match.Groups["obj"].Value;
            if (privileges == "USAGE" && target == "*.*") continue;
            if (privileges == "SELECT" && expected.Contains(target)) { seen.Add(target); continue; }
            violations.Add(line);
        }
        violations.AddRange(expected.Where(t => !seen.Contains(t)).Select(t => $"누락: SELECT ON {t}"));
        return violations;
    }

    /// <summary>공개 연결 문자열에서 롤 이름을 꺼내 검증한다.</summary>
    /// <param name="publicConnectionString"><c>ConnectionStrings:Public</c> 값.</param>
    /// <returns>검증된 롤 이름.</returns>
    /// <exception cref="InvalidOperationException">연결 문자열 형식이 잘못됐거나, 사용자 이름이 없거나 <c>^[a-z_][a-z0-9_]{0,62}\z</c>에 맞지 않을 때. 메시지에 연결 문자열 값은 넣지 않는다.</exception>
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
        NpgsqlConnectionStringBuilder parsed;
        try
        {
            parsed = new NpgsqlConnectionStringBuilder(publicConnectionString);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new InvalidOperationException($"설정 ConnectionStrings:Public 이(가) 잘못되었습니다: {ex.Message}", ex);
        }
        var role = parsed.Username;
        return !string.IsNullOrEmpty(role) && RoleNamePattern().IsMatch(role)
            ? role
            : throw new InvalidOperationException("ConnectionStrings:Public 의 Username 은 소문자·숫자·밑줄로 된 롤 이름이어야 합니다.");
    }

    /// <summary>롤에 적용할 문장을 순서대로 만든다: 관리 롤이 소유한 테이블마다 <c>PUBLIC</c>·해당 롤의 권한을 전부 회수 → 스키마 사용 →
    /// 이미 존재하는 허용 테이블에 <c>SELECT</c>.</summary>
    /// <param name="role"><see cref="RoleOf"/>가 검증한 롤 이름.</param>
    /// <param name="ownedTables">권한을 회수할 대상 테이블(관리 롤이 소유한 <c>public</c> 스키마 테이블 전체). 남의 소유 테이블은 이 목록에 없어야
    /// <see cref="Apply"/>가 그 테이블에 대해 권한 없는 <c>REVOKE</c>를 시도하지 않는다(그러면 42501로 트랜잭션 전체가 실패한다).</param>
    /// <returns>한 트랜잭션에서 순서대로 실행할 SQL 문장들.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 소유 테이블 수 × 2 + 1 + 존재하는 허용 테이블 수만큼의 문자열과 리스트 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// 테이블별로(스키마 전체가 아니라) 회수하는 이유: <c>ON ALL TABLES IN SCHEMA public</c>은 발행자(관리 롤)가 권한을 갖지 못한
    /// 테이블(예: 다른 롤이 만든 테이블)을 만나면 전체가 실패한다 — 관리 롤이 실제로 소유한 테이블만 대상으로 좁혀 그 실패를 막는다.
    /// <c>PUBLIC</c>도 함께 회수하는 이유: <c>REVOKE … FROM {role}</c>은 그 롤에 직접 부여된 권한만 지운다. 누군가 실수로
    /// <c>GRANT … TO PUBLIC</c>을 했다면 공개 롤은 <c>PUBLIC</c>의 일원이므로 그 권한을 그대로 물려받는다.
    /// 허용 테이블 중 아직 소유 목록에 없는 것(마이그레이션 전)은 <c>GRANT</c>를 건너뛴다 — 존재하지 않는 테이블에 대한 <c>GRANT</c>는
    /// 42P01로 기동을 막기 때문이다.
    /// </remarks>
    public static IReadOnlyList<string> BuildStatements(string role, IReadOnlyList<string> ownedTables)
    {
        if (!RoleNamePattern().IsMatch(role)) throw new ArgumentException("검증되지 않은 롤 이름", nameof(role));
        var statements = new List<string>();
        foreach (var table in ownedTables)
        {
            var quoted = QuoteIdentifier(table);
            statements.Add($"REVOKE ALL ON {quoted} FROM PUBLIC");
            statements.Add($"REVOKE ALL ON {quoted} FROM {role}");
        }
        statements.Add($"GRANT USAGE ON SCHEMA public TO {role}");
        var owned = new HashSet<string>(ownedTables, StringComparer.Ordinal);
        foreach (var table in ReadableTables)
        {
            // 허용 테이블이지만 아직 마이그레이션되지 않아 소유 목록에 없으면(정상 배포 흐름에서는 일어나지 않는다 — Apply는
            // Migrate() 직후에만 호출된다) 그 GRANT는 건너뛴다.
            if (!owned.Contains(table)) continue;
            statements.Add($"GRANT SELECT ON {QuoteIdentifier(table)} TO {role}");
        }
        return statements;
    }

    /// <summary>테이블 이름을 SQL 식별자로 인용한다(<c>"</c> → <c>""</c>).</summary>
    /// <param name="identifier">인용할 식별자.</param>
    /// <returns>큰따옴표로 감싼 식별자.</returns>
    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>관리 연결(테이블 소유자)로 권한을 다시 맞춘다. 마이그레이션 직후에 부른다 — 새 테이블이 생긴 뒤여야 한다.</summary>
    /// <param name="db">테이블 소유자 롤로 접속하는 관리 컨텍스트.</param>
    /// <param name="publicConnectionString"><c>ConnectionStrings:Public</c> 값.</param>
    /// <exception cref="InvalidOperationException">공개 롤이 관리 롤과 같을 때(권한을 회수하면 앱이 자기 테이블을 못 읽는다), 또는 롤 이름이 잘못됐을 때.</exception>
    /// <exception cref="Npgsql.PostgresException">관리 롤이 소유하지 않은 테이블에 대해 권한을 회수하려 하거나(42501), 그 밖의 서버 오류가 났을 때.
    /// <see cref="BuildStatements"/>가 소유 테이블만 대상으로 좁히므로 정상 배포에서는 발생하지 않는다.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe(<paramref name="db"/>가 단일 스레드 전용). 시작 시 1회 호출.</description></item>
    /// <item><description><b>Memory Allocation:</b> 소유 테이블 이름 목록 1개와 <see cref="BuildStatements"/>의 문자열들.</description></item>
    /// <item><description><b>Blocking:</b> 동기 블로킹. 소유 테이블 조회 1회 + 트랜잭션 하나에서 문장 몇 개를 실행한다 — 중간에 실패하면 권한은 이전 상태로 남는다.</description></item>
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
        // 관리 롤(현재 세션의 current_user)이 소유한 테이블만 회수 대상으로 삼는다 — 남의 소유 테이블이 스키마에 섞여 있어도
        // (예: 점검용 계정이 만든 테이블) REVOKE가 그 테이블을 건드리지 않아 42501로 기동이 막히지 않는다.
        var ownedTables = db.Database.SqlQueryRaw<string>(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tableowner = current_user").ToList();
        foreach (var statement in BuildStatements(role, ownedTables))
        {
            // 문장은 검증된 식별자와 상수 목록으로만 조립된다(사용자 입력 없음). GRANT의 대상 식별자는 SQL 매개변수가 될 수 없다.
#pragma warning disable EF1002
            db.Database.ExecuteSqlRaw(statement);
#pragma warning restore EF1002
        }
        transaction.Commit();
    }
}
