using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>공개 조회 전용 DB 사용자(<c>ConnectionStrings:Public</c>의 사용자)에게 공개 페이지가 읽는 테이블의 <c>SELECT</c>만 부여하고, 그 결과를 공개 연결 자신의 <c>SHOW GRANTS</c>로 검증한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다. <see cref="Apply"/>는 시작 스레드에서 마이그레이션 직후 1회만 호출된다(단일 인스턴스 배포 — 스펙 3.10).</description></item>
/// <item><description><b>Memory Allocation:</b> 시작 시 <c>GRANT</c> 문장 5개와 <c>SHOW GRANTS</c> 행 목록을 할당한다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="Apply"/>는 동기 DB 왕복(GRANT 5회 + 공개 연결의 세션 설정·SHOW GRANTS 2회)이다. 요청 처리 경로에서 부르지 않는다.</description></item>
/// </list>
/// 테이블 목록을 EF 모델에서 뽑지 않는 이유: <see cref="PublicDbContext"/>는 <see cref="AppDbContext"/>를 상속해 모델에
/// <c>AdminState</c>(세션 폐기 카운터)와 마이그레이션 이력까지 들어 있다. 공개 사용자가 읽어도 되는 것은 아래 목록뿐이다.
/// </remarks>
public static partial class PublicRoleGrants
{
    /// <summary>공개 사용자가 읽을 수 있는 테이블. 공개 페이지·피드·첨부 GET이 실제로 조회하는 것만 둔다.</summary>
    public static readonly IReadOnlyList<string> ReadableTables = ["Posts", "Series", "Tags", "PostTags", "Attachments"];

    // MySQL 사용자 이름 한도 32자. 소문자·숫자·밑줄만 허용해 GRANT 문장에 인용 없이 넣을 수 있게 한다.
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,31}\\z")]
    private static partial Regex UserNamePattern();

    // DB 이름은 백틱 인용하지만, 인용 탈출을 원천 차단하려고 형식도 제한한다.
    [GeneratedRegex("^[A-Za-z0-9_]{1,64}\\z")]
    private static partial Regex DatabaseNamePattern();

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

    /// <summary>공개 연결 문자열에서 사용자 이름을 꺼내 검증한다.</summary>
    /// <param name="publicConnectionString"><c>ConnectionStrings:Public</c> 값.</param>
    /// <returns>검증된 사용자 이름.</returns>
    /// <exception cref="InvalidOperationException">연결 문자열 형식이 잘못됐거나, <c>User ID</c>가 없거나 <c>^[a-z_][a-z0-9_]{0,31}\z</c>에 맞지 않을 때. 메시지에 연결 문자열 값은 넣지 않는다.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 연결 문자열 파서 1개와 결과 문자열.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음 — <c>StartupValidation</c>에서도 부를 수 있다.</description></item>
    /// </list>
    /// 사용자 이름은 SQL 매개변수가 될 수 없어 GRANT 문장에 직접 들어가므로 형식을 좁게 제한한다(MySQL 사용자 이름 한도 32자).
    /// </remarks>
    public static string RoleOf(string publicConnectionString)
    {
        MySqlConnectionStringBuilder parsed;
        try
        {
            parsed = new MySqlConnectionStringBuilder(publicConnectionString);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // 예외 메시지에 연결 문자열 조각이 들어갈 수 있어 옮기지 않는다(StartupValidationTests가 비노출을 고정).
            throw new InvalidOperationException("설정 ConnectionStrings:Public 이(가) 잘못되었습니다(연결 문자열 형식).", ex);
        }
        var user = parsed.UserID;
        return !string.IsNullOrEmpty(user) && UserNamePattern().IsMatch(user)
            ? user
            : throw new InvalidOperationException("ConnectionStrings:Public 의 User ID 는 소문자·숫자·밑줄로 된 32자 이하 사용자 이름이어야 합니다.");
    }

    /// <summary>공개 사용자에게 허용 테이블마다 <c>SELECT</c>를 주는 <c>GRANT</c> 문장을 만든다.</summary>
    /// <param name="user"><see cref="RoleOf"/>가 검증한 사용자 이름.</param>
    /// <param name="database">대상 DB 이름(관리 연결의 현재 DB).</param>
    /// <returns><see cref="ReadableTables"/> 순서의 <c>GRANT SELECT ON `db`.`table` TO 'user'@'%'</c> 문장들.</returns>
    /// <exception cref="ArgumentException"><paramref name="user"/>나 <paramref name="database"/>가 허용 형식이 아닐 때(인용 탈출 차단).</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 문자열 5개와 리스트 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// <c>GRANT</c>는 멱등이라 기동마다 다시 실행해도 된다. MySQL은 존재하지 않는 테이블에 대한 테이블 단위 GRANT를 거부하므로(1146) 마이그레이션 뒤에만 부른다.
    /// </remarks>
    public static IReadOnlyList<string> BuildGrantStatements(string user, string database)
    {
        if (!UserNamePattern().IsMatch(user)) throw new ArgumentException("검증되지 않은 사용자 이름", nameof(user));
        if (!DatabaseNamePattern().IsMatch(database)) throw new ArgumentException("검증되지 않은 DB 이름", nameof(database));
        return ReadableTables.Select(table => $"GRANT SELECT ON `{database}`.`{table}` TO '{user}'@'%'").ToList();
    }

    /// <summary>관리 연결로 공개 사용자에게 SELECT를 부여하고, 공개 연결의 <c>SHOW GRANTS</c>로 권한 집합이 정확한지 검증한다. 마이그레이션 직후에 부른다.</summary>
    /// <param name="admin">관리 사용자(<c>blog_app</c>, <c>blog.*</c> 한정 GRANT OPTION 보유)로 접속하는 컨텍스트.</param>
    /// <param name="publicDb">공개 사용자로 접속하는 컨텍스트. 검증에만 쓴다.</param>
    /// <exception cref="InvalidOperationException">공개 사용자가 관리 사용자와 같을 때, 사용자 이름이 잘못됐을 때, 또는 권한 집합이 허용 목록과 다를 때(초과·누락 모두).</exception>
    /// <exception cref="MySqlException">GRANT가 서버에서 실패했을 때(예: 관리 사용자에게 GRANT OPTION이 없음 — 1044/1142).</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe(두 컨텍스트가 단일 스레드 전용). 기동 시 시작 스레드에서 1회 호출.</description></item>
    /// <item><description><b>Memory Allocation:</b> GRANT 문장 5개와 <c>SHOW GRANTS</c> 행 목록 1개.</description></item>
    /// <item><description><b>Blocking:</b> <b>동기</b> DB 왕복 5+2회(GRANT 5회 + 공개 연결의 세션 설정 1회·SHOW GRANTS 1회). 기동 경로 전용이며 요청 경로에서 부르지 않는다.</description></item>
    /// </list>
    /// 앱이 GRANT를 적용하는 이유(스펙 R1): 테이블은 기동 시 마이그레이션으로 생기고 MySQL은 없는 테이블에 GRANT를 거부하므로, 운영자가 미리 줄 수 없다.
    /// 관리 사용자의 GRANT OPTION은 자기가 가진 <c>blog.*</c> 권한을 넘기는 것만 허용해 순증 위험이 없다.
    /// 초과 권한은 자동 회수하지 않는다(스펙 R6): 회수하려면 서버 출력 문자열을 SQL로 되돌려야 하고, 초과 권한이 생기는 경로는 사람의 수동 GRANT뿐이라 조용히 고치기보다 기동 실패로 드러낸다.
    /// <c>SHOW GRANTS</c>(인자 없음)는 권한 없이 자기 자신에 대해 항상 허용되므로 관리 사용자에게 <c>mysql.*</c> 조회 권한이 필요 없다.
    /// </remarks>
    public static void Apply(AppDbContext admin, PublicDbContext publicDb)
    {
        var user = RoleOf(publicDb.Database.GetConnectionString()!);
        var adminUser = new MySqlConnectionStringBuilder(admin.Database.GetConnectionString()!).UserID;
        if (string.Equals(user, adminUser, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ConnectionStrings:Public 의 User ID 가 ConnectionStrings:Default 와 같습니다 — 공개 조회는 별도의 읽기 전용 사용자여야 합니다.");
        }
        var database = admin.Database.GetDbConnection().Database;
        foreach (var statement in BuildGrantStatements(user, database))
        {
            // 문장은 형식 검증된 식별자와 상수 목록으로만 조립된다(사용자 입력 없음). GRANT 대상은 SQL 매개변수가 될 수 없다.
#pragma warning disable EF1002
            admin.Database.ExecuteSqlRaw(statement);
#pragma warning restore EF1002
        }
        var lines = new List<string>();
        publicDb.Database.OpenConnection(); // 인터셉터가 공개 세션 설정을 건다. SHOW GRANTS는 자기 자신에 대해 항상 허용된다.
        try
        {
            using var command = publicDb.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SHOW GRANTS";
            using var reader = command.ExecuteReader();
            while (reader.Read()) lines.Add(reader.GetString(0));
        }
        finally
        {
            publicDb.Database.CloseConnection();
        }
        var violations = Violations(lines, database, ReadableTables);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException("공개 조회 사용자의 권한이 허용 목록과 다릅니다(자동 회수하지 않음 — 운영자가 REVOKE 후 재기동): " + string.Join(" | ", violations));
        }
    }
}
