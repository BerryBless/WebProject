using System.Text;
using PortfolioBlog.Api.Features.Attachments;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary><see cref="AttachmentEndpoints.DisplayName"/> 순수 함수 단위 테스트. 서러게이트 쌍이 잘리거나 애초에 홀로 있는 경계 조건을 집중적으로 검증한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 순수 정적 함수만 호출하며 공유 상태가 없다. 병렬 실행에 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 테스트당 짧은 문자열 몇 개.</description></item>
/// <item><description><b>Blocking:</b> 없음. DB·Docker 불필요.</description></item>
/// </list>
/// </remarks>
public sealed class DisplayNameTests
{
    /// <summary><paramref name="value"/>에 홀로 남은(짝 없는) UTF-16 서러게이트가 있는지 검사한다: 상위 서러게이트 뒤에 하위 서러게이트가
    /// 없거나, 하위 서러게이트 앞에 상위 서러게이트가 없으면 그 문자는 완전한 코드 포인트를 이루지 못한다.</summary>
    /// <param name="value">검사할 문자열.</param>
    /// <returns>홀로 남은 서러게이트가 하나라도 있으면 <see langword="true"/>.</returns>
    private static bool HasUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return true;
                i++; // 짝을 확인했으니 하위 서러게이트 자리는 건너뛴다(다음 반복에서 다시 검사하지 않는다)
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return true; // 앞 문자가 상위 서러게이트였다면 위 분기에서 이미 건너뛰었을 것이다 — 여기 도달했다면 홀로 있는 것
            }
        }
        return false;
    }

    // 서러게이트가 포함된 리터럴을 [InlineData]에 직접 넣지 않는다: xUnit이 테스트 표시 이름에 인자를 직렬화해 trx(XML)로 쓰는데
    // XML 1.0은 홀로 남은 서러게이트를 표현할 수 없다 — 케이스 이름(ASCII)만 데이터로 넘기고 실제 문자열은 메서드 본문에서 만든다.
    private static string UploadedNameFor(string caseName) => caseName switch
    {
        "surrogate-split-by-truncation" => new string('a', 249) + "\U0001F600" + "bbbb.webp", // 리뷰어의 재현 케이스: 자르는 지점이 서러게이트 쌍 한가운데
        "lone-high-surrogate" => "\uD83D" + ".png",
        "lone-low-surrogate-middle" => "abc" + "\uDC00" + "def.png",
        "emoji-well-under-limit" => "short-name-" + "\U0001F600" + ".webp",
        "dot-space-dot-no-truncation" => "a. .png", // fix round 2, B3: 자르기 없이도(길이가 한도에 한참 못 미쳐도) ".."이 생기는 사전 존재 결함
        "truncation-lands-on-dot" => new string('a', 249) + "." + "bbbbbb.webp", // fix round 2, B3: 리뷰어의 원래 재현(자르는 지점이 마침표)
        _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
    };

    /// <summary>클라이언트가 보낸 파일 이름이 어떤 모양으로 서러게이트를 담고 있어도(길이 제한에 걸려 쌍이 잘리든, 애초에 홀로 있든)
    /// 결과에는 홀로 남는 서러게이트가 없고, 길이는 255 이하이며, 확장자는 시그니처가 정한 대로다. 또한 DTO가 실제로 만드는 것과 같은 방식으로
    /// URL을 구성했을 때 <see cref="UrlPolicy.IsAllowedImage"/>를 통과한다 — 즉 결과에 <c>".."</c>이 남아 있지 않다(fix round 2, B3).</summary>
    [Theory]
    [InlineData("surrogate-split-by-truncation", ImageKind.WebP, "webp")]
    [InlineData("lone-high-surrogate", ImageKind.Png, "png")]
    [InlineData("lone-low-surrogate-middle", ImageKind.Png, "png")]
    [InlineData("emoji-well-under-limit", ImageKind.WebP, "webp")]
    [InlineData("dot-space-dot-no-truncation", ImageKind.Png, "png")]
    [InlineData("truncation-lands-on-dot", ImageKind.WebP, "webp")]
    public void DisplayName_NeverLeavesAnUnpairedSurrogate(string caseName, ImageKind kind, string extension)
    {
        var uploaded = UploadedNameFor(caseName);

        var result = AttachmentEndpoints.DisplayName(uploaded, kind);

        Assert.False(HasUnpairedSurrogate(result), $"홀로 남은 서러게이트가 있다: {caseName} → \"{result}\"");
        Assert.True(result.Length <= AppDbContext.FileNameMax, $"{caseName}: 길이 {result.Length} > {AppDbContext.FileNameMax}");
        Assert.EndsWith("." + extension, result, StringComparison.Ordinal);
        // AttachmentEndpoints.ToDto가 실제로 URL을 만드는 방식(Uri.EscapeDataString)을 그대로 흉내 낸다.
        var url = $"/attachments/{Guid.Empty}/{Uri.EscapeDataString(result)}";
        Assert.True(UrlPolicy.IsAllowedImage(url), $"{caseName}: 반환된 URL이 UrlPolicy.IsAllowedImage를 통과하지 못했다: {url} (result=\"{result}\")");
    }

    /// <summary>제한보다 훨씬 짧은 이모지는 잘리지 않고 두 절반(상위·하위 서러게이트) 모두 온전히 남는다.</summary>
    [Fact]
    public void DisplayName_EmojiWellUnderLimit_KeptIntact()
    {
        var uploaded = UploadedNameFor("emoji-well-under-limit");

        var result = AttachmentEndpoints.DisplayName(uploaded, ImageKind.WebP);

        Assert.Contains("\U0001F600", result, StringComparison.Ordinal);
    }

    /// <summary>선행 마침표 제거(<c>Trim('.')</c>)가 그 앞의 공백을 새로 드러내는 순서 때문에, 마침표·공백이 번갈아 나오는 아주 긴 입력은
    /// 자르기 지점 전체가 마침표·공백뿐인 상태로 잘릴 수 있다(측정으로 확인 — "잘린 stem의 0번 문자는 항상 안전하다"는 처음 추정은 틀렸다:
    /// <c>Trim()</c>이 <c>Trim('.')</c>보다 먼저 실행돼 마침표를 지운 뒤 드러나는 공백은 다시 다듬어지지 않는다). 그래도 결과는 안전하게
    /// "image" 폴백으로 수렴하고 확장자 앞에 <c>".."</c>이 생기지 않는다(fix round 2, B3).</summary>
    [Fact]
    public void DisplayName_LongAlternatingDotSpaceRun_FallsBackToImage()
    {
        var uploaded = string.Concat(Enumerable.Repeat(". ", 200)) + ".png";

        var result = AttachmentEndpoints.DisplayName(uploaded, ImageKind.Png);

        Assert.Equal("image.png", result);
        var url = $"/attachments/{Guid.Empty}/{Uri.EscapeDataString(result)}";
        Assert.True(UrlPolicy.IsAllowedImage(url));
    }
}
