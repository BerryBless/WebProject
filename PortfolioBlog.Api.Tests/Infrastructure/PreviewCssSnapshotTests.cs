using PortfolioBlog.Api.Infrastructure.Markdown;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>관리 SPA의 미리보기 iframe이 쓰는 CSS 스냅숏(<c>PortfolioBlog.Web/public/preview/*.css</c>)이 공개 사이트의 원본과 같은지 검사한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 픽스처를 공유하지 않는다. 읽기 전용 검사는 다른 테스트와 병렬로 돌아도 안전하다.
/// 단 환경변수 <c>UPDATE_PREVIEW_SNAPSHOTS=1</c>로 돌리면 저장소의 파일을 <b>덮어쓴다</b> — 그 모드는 이 클래스만 골라 실행하고,
/// 파일을 쓴 뒤 의도적으로 실패로 끝난다(환경변수를 끄지 않은 채로 두면 드리프트 검사가 항상 통과해 버리는 것을 막는다).</description></item>
/// <item><description><b>Memory Allocation:</b> CSS 두 파일(각 4KB 안팎)을 문자열로 읽는다.</description></item>
/// <item><description><b>Blocking:</b> 동기 파일 I/O. DB·네트워크·Docker를 쓰지 않는다.</description></item>
/// </list>
/// 미리보기는 관리 출처에서 뜨는데, 공개 사이트의 <c>/css/highlight.css</c>는 공개 호스트에만 매핑되고 운영에서는 Caddy가 관리 호스트의
/// <c>/api/*</c>·<c>/attachments/*</c>만 백엔드로 넘긴다. 그래서 SPA가 사본을 정적 파일로 들고 있고, 이 테스트가 사본이 낡는 것을 막는다.
/// </remarks>
public sealed class PreviewCssSnapshotTests
{
    /// <summary>스냅숏 갱신 모드를 켜는 환경변수 이름. 값이 정확히 "1"일 때만 갱신 모드로 동작한다.</summary>
    private const string UpdateVariable = "UPDATE_PREVIEW_SNAPSHOTS";

    /// <summary><c>site.css</c> 사본이 <c>PortfolioBlog.Api/wwwroot/css/site.css</c>와 같다(줄 끝만 무시한다 — 작업 트리의 줄 끝은 git 설정에 따라 다르다).</summary>
    [Fact]
    public void SiteCss_Snapshot_MatchesThePublicStylesheet()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "PortfolioBlog.Api", "wwwroot", "css", "site.css"));
        AssertSnapshot(Path.Combine(root, "PortfolioBlog.Web", "public", "preview", "site.css"), source);
    }

    /// <summary><c>highlight.css</c> 사본이 공개 사이트가 <c>/css/highlight.css</c>로 내보내는 값(<see cref="HighlightCss.Value"/>)과 같다.</summary>
    [Fact]
    public void HighlightCss_Snapshot_MatchesTheGeneratedStylesheet() =>
        AssertSnapshot(Path.Combine(RepositoryRoot(), "PortfolioBlog.Web", "public", "preview", "highlight.css"), HighlightCss.Value);

    private static void AssertSnapshot(string snapshotPath, string expected)
    {
        var normalized = Normalize(expected);
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllText(snapshotPath, normalized);
            // 갱신 모드는 아무것도 단언하지 않으면 환경변수를 끄는 것을 잊었을 때 드리프트 검사가 영원히 통과해 버린다.
            // 파일을 쓴 뒤 항상 실패로 끝내 다음 실행에서 반드시 환경변수를 끄고 재검증하게 만든다.
            Assert.Fail($"{Path.GetFileName(snapshotPath)} 스냅숏을 갱신했습니다. {UpdateVariable}를 끄고 다시 실행해 검증하세요.");
        }

        Assert.True(File.Exists(snapshotPath), $"스냅숏이 없습니다: {snapshotPath}. {UpdateVariable}=1로 이 테스트 클래스를 실행해 만드세요.");
        Assert.True(normalized == Normalize(File.ReadAllText(snapshotPath)),
            $"{Path.GetFileName(snapshotPath)} 스냅숏이 원본과 다릅니다. 공개 사이트 CSS를 바꿨다면 {UpdateVariable}=1로 이 테스트 클래스를 실행해 사본을 갱신하세요.");
    }

    // "\r\n"을 한 덩어리로 먼저 "\n" 하나로 바꾼다. 순서를 바꿔 "\r"부터 바꾸면 "\r\n"의 "\r"만 "\n"이 되어
    // "\n\n"(줄 하나가 빈 줄 하나로 늘어남)이 남는다. "\r\n"을 먼저 없앤 뒤에는 홀로 남은 "\r"(옛 Mac 줄 끝)만 "\n"으로 바꾸면 된다.
    private static string Normalize(string css) => css.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    // 테스트 출력 폴더(bin/Release/net10.0)에서 위로 올라가며 솔루션 파일을 찾는다. 절대 경로를 하드코딩하지 않는다(저장소 경로 규칙).
    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PortfolioBlog.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("PortfolioBlog.slnx를 찾지 못했습니다.");
    }
}
