using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 사용자의 SHOW GRANTS 출력이 허용 집합과 정확히 같은지 판정하는 규칙(스펙 D3·R6)을 고정한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처:</b> 없음(문자열 입력).</description></item>
/// <item><description><b>병렬 실행:</b> 안전.</description></item>
/// <item><description><b>외부 자원:</b> 없음.</description></item>
/// </list>
/// </remarks>
public sealed class PublicGrantViolationsTests
{
    private static readonly string[] Tables = ["Posts", "Series"];
    private const string Usage = "GRANT USAGE ON *.* TO `pub`@`%`";

    private static IReadOnlyList<string> Check(params string[] lines) => PublicRoleGrants.Violations(lines, "blog", Tables);

    /// <summary>정확히 USAGE + 허용 테이블 SELECT면 위반이 없다.</summary>
    [Fact]
    public void ExactSet_HasNoViolations() =>
        Assert.Empty(Check(Usage, "GRANT SELECT ON `blog`.`Posts` TO `pub`@`%`", "GRANT SELECT ON `blog`.`Series` TO `pub`@`%`"));

    /// <summary>Windows MySQL(lower_case_table_names=1)은 테이블 이름을 소문자로 보고한다. 이름은 대소문자 무시로 비교한다.</summary>
    [Fact]
    public void TableNames_AreComparedCaseInsensitively() =>
        Assert.Empty(Check(Usage, "GRANT SELECT ON `blog`.`posts` TO `pub`@`%`", "GRANT SELECT ON `blog`.`series` TO `pub`@`%`"));

    /// <summary>허용 테이블이 빠지면 누락으로 보고한다(공개 페이지가 1142로 깨지기 전에 기동에서 드러낸다).</summary>
    [Fact]
    public void MissingTable_IsAViolation() =>
        Assert.Contains(Check(Usage, "GRANT SELECT ON `blog`.`Posts` TO `pub`@`%`"), v => v.Contains("`blog`.`Series`", StringComparison.Ordinal));

    /// <summary>초과 권한은 전부 위반이다: 쓰기 권한, DB 단위 권한, 비허용 테이블, 다른 DB, 전역 권한, GRANT OPTION, 롤 부여.</summary>
    [Theory]
    [InlineData("GRANT SELECT, INSERT ON `blog`.`Posts` TO `pub`@`%`")]
    [InlineData("GRANT SELECT ON `blog`.* TO `pub`@`%`")]
    [InlineData("GRANT SELECT ON `blog`.`AdminState` TO `pub`@`%`")]
    [InlineData("GRANT SELECT ON `other`.`Posts` TO `pub`@`%`")]
    [InlineData("GRANT PROCESS ON *.* TO `pub`@`%`")]
    [InlineData("GRANT SELECT ON `blog`.`Series` TO `pub`@`%` WITH GRANT OPTION")]
    [InlineData("GRANT `admin_role`@`%` TO `pub`@`%`")]
    public void AnyExtraGrant_IsAViolation(string extra) =>
        Assert.Contains(extra, Check(Usage, "GRANT SELECT ON `blog`.`Posts` TO `pub`@`%`", "GRANT SELECT ON `blog`.`Series` TO `pub`@`%`", extra));
}
