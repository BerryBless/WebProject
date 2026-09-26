using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>GET_LOCK 이름 규칙(스펙 D5·R3)을 고정한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처:</b> 없음.</description></item>
/// <item><description><b>병렬 실행:</b> 안전.</description></item>
/// <item><description><b>외부 자원:</b> 없음.</description></item>
/// </list>
/// </remarks>
public sealed class AttachmentLockNameTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>MySQL 잠금 이름 한도(64자) 안에 들어간다. 넘으면 GET_LOCK이 오류를 낸다.</summary>
    [Fact]
    public void Name_FitsTheMySqlLimit() => Assert.True(AttachmentLock.NameFor("blog_test_" + new string('f', 32), Sha).Length <= 64);

    /// <summary>잠금 이름은 서버 전역이므로 DB가 다르면 같은 SHA라도 이름이 달라야 한다(테스트의 DB별 격리).</summary>
    [Fact]
    public void DifferentDatabases_GetDifferentNames() =>
        Assert.NotEqual(AttachmentLock.NameFor("blog", Sha), AttachmentLock.NameFor("blog_test_1", Sha));

    /// <summary>같은 입력은 같은 이름이다(프로세스·인스턴스가 달라도). string.GetHashCode처럼 프로세스별 시드가 있는 해시를 쓰면 깨진다.</summary>
    [Fact]
    public void SameInput_IsDeterministic()
    {
        Assert.Equal(AttachmentLock.NameFor("blog", Sha), AttachmentLock.NameFor("blog", Sha));
        Assert.Equal("att:def53e95:", AttachmentLock.NameFor("blog", Sha)[..13]); // SHA-256("blog") 앞 8자(printf blog | sha256sum으로 확인)
    }

    /// <summary>이름이 SHA의 앞 48자를 담는다. 서로 다른 내용이 같은 잠금을 쓰는 일은 192비트 충돌뿐이다.</summary>
    [Fact]
    public void Name_CarriesTheShaPrefix() => Assert.EndsWith(":" + Sha[..48], AttachmentLock.NameFor("blog", Sha), StringComparison.Ordinal);
}
