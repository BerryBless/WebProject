namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>업로드를 허용하는 이미지 형식. SVG는 스크립트를 담을 수 있어 제외한다.</summary>
public enum ImageKind
{
    /// <summary>PNG(APNG 포함).</summary>
    Png,
    /// <summary>JPEG(JFIF·Exif).</summary>
    Jpeg,
    /// <summary>GIF87a·GIF89a.</summary>
    Gif,
    /// <summary>WebP(RIFF 컨테이너).</summary>
    WebP,
}
