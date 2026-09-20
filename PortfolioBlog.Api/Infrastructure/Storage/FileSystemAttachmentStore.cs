using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>업로드가 <see cref="AttachmentOptions.MaxBytes"/> 한도를 넘었다. 호출부(업로드 엔드포인트)는 이를 413으로 바꾼다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 예외 인스턴스이며 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 예외 인스턴스 1개(+ 스택 트레이스). 정상 업로드 경로의 비용은 0이다.</description></item>
/// <item><description><b>Blocking:</b> 해당 없음. 예외 타입 자체는 코드를 실행하지 않는다.</description></item>
/// </list>
/// </remarks>
public sealed class AttachmentTooLargeException() : Exception("첨부 크기 한도를 넘었다.");

/// <summary>업로드된 바이트가 허용 시그니처(PNG·JPEG·GIF·WebP) 밖이거나 <see cref="MetadataStripper.Strip"/>이 구조 오류로 거부했다. 호출부는 이를 415로 바꾼다.</summary>
/// <param name="message">사용자·로그에 보여줄 한국어 메시지.</param>
/// <param name="inner"><see cref="MetadataStripper.Strip"/>이 던진 원본 <see cref="InvalidDataException"/>(시그니처 미판정이면 <see langword="null"/>).</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 예외 인스턴스이며 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 예외 인스턴스 1개(+ 스택 트레이스). 정상 업로드 경로의 비용은 0이다.</description></item>
/// <item><description><b>Blocking:</b> 해당 없음. 예외 타입 자체는 코드를 실행하지 않는다.</description></item>
/// </list>
/// </remarks>
public sealed class UnsupportedImageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>저장 결과.</summary>
/// <param name="Kind">시그니처로 판정한 형식.</param>
/// <param name="Sha256">메타데이터 제거 후 바이트의 SHA-256(소문자 hex).</param>
/// <param name="SizeBytes">메타데이터 제거 후 크기.</param>
/// <param name="StoragePath">루트 기준 상대 경로(<c>ab/abcdef….png</c>, 구분자는 항상 <c>/</c>).</param>
public sealed record StoredImage(ImageKind Kind, string Sha256, long SizeBytes, string StoragePath);

/// <summary>첨부를 로컬 볼륨에 내용 주소 방식으로 저장한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 필드는 생성 후 불변이고, 같은 내용의 동시 업로드는 "먼저 옮긴 쪽이 이기고 나머지는 자기 임시 파일을 지운다"로 수렴한다(<c>File.Move(overwrite: false)</c>).</description></item>
/// <item><description><b>Memory Allocation:</b> 업로드 크기와 무관하게 64KB 풀 버퍼. 10MB를 메모리에 올리지 않는다(LOH 회피).</description></item>
/// <item><description><b>Blocking:</b> 업로드 수신은 비동기 I/O. 메타데이터 제거와 해시는 임시 파일에 대한 동기 I/O다(최대 10MB, 관리 표면 전용).</description></item>
/// </list>
/// 경로는 전부 서버가 만든다: 임시 파일 이름은 Guid, 최종 경로는 SHA-256. 업로드된 파일 이름은 이 클래스에 들어오지도 않는다.
/// </remarks>
public sealed class FileSystemAttachmentStore
{
    private const int BufferSize = 64 * 1024;
    private readonly string _root;
    private readonly string _temp;

    /// <summary><c>Attachments:RootPath</c> 설정으로 저장 루트를 계산한다.</summary>
    /// <param name="options">저장 루트 설정.</param>
    /// <param name="environment">콘텐츠 루트 경로를 얻기 위한 호스팅 환경(상대 경로 기준).</param>
    /// <exception cref="InvalidOperationException"><c>RootPath</c>가 비어 있을 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 싱글턴 등록으로 앱 시작 시 1회만 호출된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 경로 문자열 계산에 따른 시작 시 1회성 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음(디렉터리 생성은 <see cref="SaveAsync"/> 첫 호출로 지연된다).</description></item>
    /// </list>
    /// </remarks>
    public FileSystemAttachmentStore(IOptions<AttachmentOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.RootPath;
        if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("Attachments:RootPath 설정이 없습니다.");
        _root = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));
        _temp = Path.Combine(_root, ".tmp");
    }

    /// <summary>저장 루트 기준 상대 경로를 실제 파일 시스템 경로로 바꾼다.</summary>
    /// <param name="storagePath">서버가 만든 상대 경로(<see cref="Attachment.StoragePath"/>).</param>
    /// <returns>저장 루트 밑의 절대 경로.</returns>
    /// <exception cref="InvalidOperationException">계산된 경로가 저장 루트를 벗어날 때(DB 값이 손상된 경우의 방어).</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 인스턴스 필드는 읽기 전용이다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 경로 문자열 1~2개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public string PhysicalPath(string storagePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, storagePath.Replace('/', Path.DirectorySeparatorChar)));
        // DB 값이 손상됐더라도 루트 밖을 가리키면 읽지 않는다.
        return full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : throw new InvalidOperationException("첨부 경로가 저장 루트를 벗어난다.");
    }

    /// <summary>저장 루트 기준 상대 경로가 가리키는 파일을 지운다.</summary>
    /// <param name="storagePath">지울 파일의 상대 경로.</param>
    /// <returns>삭제에 성공했으면 <see langword="true"/>. 파일이 이미 없어도 <see langword="true"/>다(<see cref="File.Delete(string)"/>는 없는 파일에 대해 예외를 던지지 않는다).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 파일 시스템 자체의 원자적 삭제에 의존한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 경로 문자열 계산 외 추가 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 동기 파일 I/O. 호출부(관리 표면 전용)가 짧은 지연을 감수한다.</description></item>
    /// </list>
    /// </remarks>
    public bool TryDelete(string storagePath)
    {
        try
        {
            File.Delete(PhysicalPath(storagePath));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>업로드 스트림을 받아 크기 한도·시그니처 판정·메타데이터 제거·해시 계산을 거쳐 내용 주소 경로에 저장한다.</summary>
    /// <param name="upload">클라이언트가 보낸 원시 업로드 스트림. 소유권은 호출자에게 있다.</param>
    /// <param name="ct">취소 토큰.</param>
    /// <returns>저장된 이미지의 형식·해시·크기·상대 경로.</returns>
    /// <exception cref="AttachmentTooLargeException">수신 도중 <see cref="AttachmentOptions.MaxBytes"/>를 넘었을 때.</exception>
    /// <exception cref="UnsupportedImageException">시그니처가 네 형식 중 어디에도 맞지 않거나 <see cref="MetadataStripper.Strip"/>이 구조 오류로 거부했을 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 동시 호출은 서로 다른 임시 파일(Guid 이름)을 쓰므로 간섭하지 않고, 최종 이동은 <c>overwrite: false</c>라 같은 내용의 경쟁은 한쪽만 이긴다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="ArrayPool{T}.Shared"/>의 64KB 버퍼 하나. 업로드 크기를 메모리에 올리지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 수신은 비동기(<see cref="ReceiveAsync"/>). 시그니처 판정·메타데이터 제거·해싱은 임시 파일에 대한 동기 I/O다(최대 10MB, 이 클래스 호출부는 관리 표면 전용이라 요청 스레드가 짧게 막혀도 처리량에 영향이 없다).</description></item>
    /// </list>
    /// </remarks>
    public async Task<StoredImage> SaveAsync(Stream upload, CancellationToken ct)
    {
        Directory.CreateDirectory(_temp);
        var rawPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".upload");
        var cleanPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".clean");
        try
        {
            await ReceiveAsync(upload, rawPath, ct);

            ImageKind kind;
            // FileStream(FileOptions.SequentialScan): OS 미리 읽기 힌트. 제거기는 앞에서 뒤로 한 번만 읽는다.
            using (var raw = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.None, BufferSize, FileOptions.SequentialScan))
            using (var clean = new FileStream(cleanPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize))
            {
                Span<byte> header = stackalloc byte[ImageSignature.HeaderLength];
                var read = raw.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                kind = ImageSignature.Detect(header[..read]) ?? throw new UnsupportedImageException("PNG·JPEG·GIF·WebP 이미지만 올릴 수 있습니다.");
                raw.Position = 0;
                try { MetadataStripper.Strip(kind, raw, clean); }
                catch (InvalidDataException ex) { throw new UnsupportedImageException("이미지 파일 구조가 손상됐습니다.", ex); }
            }

            string sha;
            long size;
            using (var clean = new FileStream(cleanPath, FileMode.Open, FileAccess.Read, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                size = clean.Length;
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(clean, ct));
            }

            var relative = $"{sha[..2]}/{sha}.{ImageSignature.Extension(kind)}";
            var final = PhysicalPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            if (!File.Exists(final))
            {
                try { File.Move(cleanPath, final, overwrite: false); }
                catch (IOException) when (File.Exists(final)) { /* 같은 내용의 동시 업로드가 먼저 옮겼다 — 내용이 같으므로 그 파일을 쓴다 */ }
            }
            return new StoredImage(kind, sha, size, relative);
        }
        finally
        {
            File.Delete(rawPath);   // 없으면 아무 일도 하지 않는다
            File.Delete(cleanPath);
        }
    }

    /// <summary>업로드 스트림을 64KB 단위로 임시 파일에 받으며, 누적 크기가 한도를 넘으면 즉시 중단한다.</summary>
    /// <param name="upload">읽어들일 원본 업로드 스트림.</param>
    /// <param name="path">받은 바이트를 쓸 임시 파일 경로.</param>
    /// <param name="ct">취소 토큰.</param>
    /// <exception cref="AttachmentTooLargeException">누적 수신 바이트가 <see cref="AttachmentOptions.MaxBytes"/>를 넘었을 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 호출마다 독립된 버퍼·파일을 쓴다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="ArrayPool{T}.Shared"/>에서 빌린 64KB 버퍼 하나만 쓰고 반환한다. 업로드 크기와 무관하게 상수 메모리다.</description></item>
    /// <item><description><b>Blocking:</b> 완전 비동기(<see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>/<see cref="FileStream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>). 요청 스레드를 막지 않는다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task ReceiveAsync(Stream upload, string path, CancellationToken ct)
    {
        // ArrayPool<byte>.Shared: 스레드별 캐시를 먼저 보는 버킷 풀. 64KB는 LOH 임계(85,000바이트) 아래다.
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            long total = 0;
            int read;
            while ((read = await upload.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                total += read;
                if (total > AttachmentOptions.MaxBytes) throw new AttachmentTooLargeException();
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
