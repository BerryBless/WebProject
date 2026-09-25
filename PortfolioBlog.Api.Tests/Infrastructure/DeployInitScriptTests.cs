namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>배포 init 스크립트의 관리 사용자 권한이 테스트가 검증한 권한(<see cref="MySqlContainerFixture.AppPrivileges"/>)과 같다(드리프트 방지).</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처:</b> 없음(파일 읽기).</description></item>
/// <item><description><b>병렬 실행:</b> 안전.</description></item>
/// <item><description><b>외부 자원:</b> 저장소의 deploy/mysql-init/10-users.sh.</description></item>
/// </list>
/// </remarks>
public sealed class DeployInitScriptTests
{
    /// <summary>스크립트가 같은 권한 문자열과 GRANT OPTION, TLS 강제를 담고 있고 FILE·SUPER 같은 전역 권한은 없다.</summary>
    [Fact]
    public void InitScript_GrantsExactlyTheTestedPrivileges()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PortfolioBlog.slnx"))) root = root.Parent;
        var script = File.ReadAllText(Path.Combine(root!.FullName, "deploy", "mysql-init", "10-users.sh"));
        Assert.Contains($"GRANT {MySqlContainerFixture.AppPrivileges} ON `blog`.* TO 'blog_app'@'%' WITH GRANT OPTION", script, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(script, "REQUIRE SSL").Count);
        Assert.DoesNotContain("ON *.*", script, StringComparison.Ordinal);
    }
}
