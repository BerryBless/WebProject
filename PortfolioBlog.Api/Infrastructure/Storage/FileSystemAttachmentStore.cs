using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>업로드가 <see cref="AttachmentOptions.MaxBytes"/> 한도를 넘었다. 호출부(업로드 엔드포인트)는 이를 413으로 바꾼다.</summary>
public sealed class AttachmentTooLargeException() : Exception("첨부 크기 한도를 넘었다.");

/// <summary>업로드된 바이트가 허용 시그니처(PNG·JPEG·GIF·WebP) 밖이거나 <see cref="MetadataStripper.Strip"/>이 구조 오류로 거부했다. 호출부는 이를 415로 바꾼다.</summary>
/// <param name="message">사용자·로그에 보여줄 한국어 메시지.</param>
/// <param name="inner"><see cref="MetadataStripper.Strip"/>이 던진 원본 <see cref="InvalidDataException"/>(시그니처 미판정이면 <see langword="null"/>).</param>
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
    private readonly string _configuredRootPath; // 오류 메시지용 원본 설정값(운영 파일 시스템의 절대 경로 구조는 로그·예외 메시지에 남기지 않는다)
    private readonly ILogger<FileSystemAttachmentStore> _logger;

    /// <summary><c>Attachments:RootPath</c> 설정으로 저장 루트를 계산한다.</summary>
    /// <param name="options">저장 루트 설정.</param>
    /// <param name="environment">콘텐츠 루트 경로를 얻기 위한 호스팅 환경(상대 경로 기준).</param>
    /// <param name="logger">임시 파일 정리 실패 등 사용자에게 노출하지 않는 경고를 남기는 로거.</param>
    /// <exception cref="InvalidOperationException"><c>RootPath</c>가 비어 있거나, 경로로 쓸 수 없는 값(NUL 문자 등, <see cref="Path.GetFullPath(string)"/>가
    /// 거부하는 모든 경우)일 때. 후자는 원인 예외(<see cref="ArgumentException"/> 등)를 <see cref="Exception.InnerException"/>으로 보존한다.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 싱글턴 등록으로 앱 시작 시 1회만 호출된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 경로 문자열 계산에 따른 시작 시 1회성 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음(이 생성자 자체는 경로 계산만 한다 — 디렉터리를 실제로 만들고 쓰기 가능한지 확인하는
    /// I/O는 <see cref="EnsureRootIsWritable"/>이 시작 시퀀스에서 한 번 수행하고, <see cref="SaveAsync"/>는 매 호출 <c>.tmp</c> 하위 디렉터리를 확인한다).</description></item>
    /// </list>
    /// </remarks>
    public FileSystemAttachmentStore(IOptions<AttachmentOptions> options, IHostEnvironment environment, ILogger<FileSystemAttachmentStore> logger)
    {
        var configured = options.Value.RootPath;
        if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("Attachments:RootPath 설정이 없습니다.");
        _configuredRootPath = configured;
        try
        {
            // TrimEndingDirectorySeparator: Path.GetFullPath는 입력에 있던 끝 구분자를 그대로 남긴다("/data/attachments/"
            // → "/data/attachments/"). 남겨 두면 PhysicalPath의 "루트 + 구분자" 접두사 비교가 절대 매치되지 않아
            // (구분자가 중복된다) 모든 업로드·공개 GET이 InvalidOperationException으로 500이 된다 — 드라이브/파일 시스템
            // 루트("C:\", "/")는 TrimEndingDirectorySeparator가 예외적으로 그대로 둔다(PhysicalPath가 그 경우를 따로 처리한다).
            _root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured)));
        }
        catch (Exception ex)
        {
            // Path.GetFullPath는 NUL 등 경로로 쓸 수 없는 문자에 ArgumentException을 던지는데, 그 메시지는 어느 설정 키가 문제인지
            // 말해주지 않는다 — EnsureRootIsWritable과 같은 방식으로 설정 키를 명시한 실패로 통일한다. 설정값 원문은 메시지에 넣지 않는다
            // (경로로 거부된 값이라 NUL 등 안전하지 않은 문자를 그대로 담고 있을 수 있다).
            throw new InvalidOperationException("설정 Attachments:RootPath이(가) 유효한 경로가 아닙니다.", ex);
        }
        _temp = Path.Combine(_root, ".tmp");
        _logger = logger;
    }

    /// <summary>저장 루트 디렉터리가 존재하는지 확인하고(없으면 만들고) 실제로 쓸 수 있는지 0바이트 확인 파일을 만들었다 지워 검증한다.</summary>
    /// <exception cref="InvalidOperationException">디렉터리를 만들거나 확인 파일을 쓸 수 없을 때(원인이 무엇이든 — <see cref="IOException"/>·
    /// <see cref="UnauthorizedAccessException"/>뿐 아니라 잘못된 경로 문자로 인한 <see cref="ArgumentException"/>·<see cref="NotSupportedException"/> 등도 포함).
    /// 메시지는 설정 키와 설정값 원문만 담는다 — 서버가 계산한 절대 경로(운영 파일 시스템 구조)는 포함하지 않는다. 원인은 <see cref="Exception.InnerException"/>으로 보존한다.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 시작 시퀀스에서 단일 스레드로 1회 호출하도록 의도했다. 동시에 호출해도 확인 파일 이름이
    /// 매번 새 Guid라 서로 간섭하지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 경로 문자열 몇 개, 실패 시 예외 메시지 1개.</description></item>
    /// <item><description><b>Blocking:</b> 동기 파일 I/O(디렉터리 생성 + 0바이트 파일 생성·삭제). 시작 시 한 번만 호출한다 — 요청 처리 경로에서는 호출하지 않는다.</description></item>
    /// </list>
    /// </remarks>
    public void EnsureRootIsWritable()
    {
        string? probe = null;
        try
        {
            Directory.CreateDirectory(_root);
            probe = Path.Combine(_root, ".startup-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, []);
            // 루트 계산 자체가 잘못됐으면(트레일링 구분자 등) 업로드 요청이 아니라 여기서, 시작 시점에 터지게 한다.
            // 결과는 쓰지 않는다 — 이 호출의 목적은 PhysicalPath가 예외를 던지지 않는지 확인하는 것뿐이다.
            _ = PhysicalPath("00/startup-probe");
        }
        catch (Exception ex)
        {
            // 원인을 좁혀서 가리지 않는다: IOException·UnauthorizedAccessException뿐 아니라 경로 자체가 잘못된 경우(NUL 등)
            // .NET이 던지는 ArgumentException·NotSupportedException도 전부 같은 방식으로 시작을 실패시켜야, "RootPath가 뭐든 잘못되면
            // 부팅이 막힌다"가 예외 종류에 따라 조용히 빠져나가는 구멍 없이 참이 된다.
            throw new InvalidOperationException(
                $"설정 Attachments:RootPath('{_configuredRootPath}')이 가리키는 디렉터리를 만들거나 쓸 수 없습니다.", ex);
        }
        finally
        {
            if (probe is not null)
            {
                // 확인 파일 정리 자체의 실패는 시작을 막을 이유가 아니다(쓰기는 이미 성공해 목적을 달성했다) — 경고만 남긴다.
                try { File.Delete(probe); }
                catch (Exception ex) { _logger.LogWarning(ex, "시작 확인 파일 정리 실패. Probe={Probe}", Path.GetFileName(probe)); }
            }
        }
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
        // _root는 보통 끝 구분자가 없다(생성자가 TrimEndingDirectorySeparator로 정리한다) — 그때는 구분자를 붙여야
        // 접두사 비교가 "…/attachments-evil"처럼 이름만 겹치는 형제 디렉터리를 오탐하지 않는다. 다만 드라이브/파일 시스템
        // 루트("C:\", "/")는 TrimEndingDirectorySeparator가 구분자를 남겨 두므로 그때는 또 붙이면 "C:\\"가 되어 버려 다시 붙이지 않는다.
        var rootWithSeparator = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        // DB 값이 손상됐더라도 루트 밖을 가리키면 읽지 않는다.
        return full.StartsWith(rootWithSeparator, StringComparison.Ordinal)
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
    /// <item><description><b>Blocking:</b> 동기 파일 I/O. 호출부(관리 표면 전용)가 짧은 지연을 감수한다. 이 파일들을 서빙하는 호출부는
    /// 파일을 <see cref="FileShare.Read"/> | <see cref="FileShare.Delete"/>로 열기 때문에, 공개 GET이 그 파일을 스트리밍하는 도중에 이 메서드를 호출해도
    /// (Windows에서 실측: 디렉터리 항목이 즉시 unlink되고, 이미 열린 핸들은 응답이 끝날 때까지 원래 바이트를 계속 읽을 수 있다 — Linux는
    /// POSIX unlink 의미상 같은 결과가 될 것으로 보이지만 이 환경에서 직접 측정하지는 않았다) 공유 위반 없이 성공한다 — 삭제 직후
    /// "먼저 지운 뒤 같은 내용을 다시 올리기"(<c>File.Move</c>가 방금 지운 이름 위로 이동, 그 사이 먼저 연 리더는 원래 바이트를 그대로 읽음)도
    /// 그대로 성공한다(Windows 실측 확인). 이 메서드가 <see langword="false"/>를 반환해 고아 파일이 남는 경우는 ACL·I/O 실패(권한 없음, 디스크 오류 등)뿐이다 —
    /// "GET이 서빙 중이라 삭제가 막힌다"는 경우는 더 이상 아니다.</description></item>
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
            // FileStream(FileOptions 기본값=동기): 제거기가 쓰는 대상. MetadataStripper.Strip은 동기 Read/Write만 쓰므로(비동기 오버로드 없음)
            // 동기 핸들이 필수다 — 오버랩(비동기) 핸들에 동기 I/O를 걸면 호출마다 대기 객체를 거쳐 오히려 느려진다. 최대 10MB 임시 파일이라 동기 쓰기 시간도 짧다.
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
                // SHA256.HashDataAsync(Stream): 스트림을 청크 단위로 읽으며 해시하는 스트리밍 API라, 바이트 배열을
                // 통째로 받는 HashData(byte[]) 오버로드와 달리 10MB 파일 전체를 먼저 메모리에 올릴 필요가 없다.
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
            // 각각 최선형으로 지운다: 첫 번째가 실패해도(잠김·권한) 두 번째 삭제 시도는 반드시 일어나고,
            // 정리 실패로 인한 새 예외가 원래 실패(413·415 등)를 가리지 않는다.
            TryDeleteTempFile(rawPath);
            TryDeleteTempFile(cleanPath);
        }
    }

    /// <summary>임시 파일 하나를 최선형으로 지운다: 실패해도 예외를 던지지 않고 경고만 남긴다.</summary>
    /// <param name="path">지울 임시 파일의 전체 경로. 로그에는 서버가 만든 파일 이름만 남기고 클라이언트가 보낸 값은 애초에 이 경로에 들어가지 않는다.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 호출마다 독립된 경로를 받는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 실패 시에만 로그 메시지 문자열을 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 파일 I/O.</description></item>
    /// </list>
    /// </remarks>
    private void TryDeleteTempFile(string path)
    {
        try { File.Delete(path); } // 없으면 아무 일도 하지 않는다
        catch (IOException) { _logger.LogWarning("임시 파일 정리 실패(잠김 등). TempFile={TempFile}", Path.GetFileName(path)); }
        catch (UnauthorizedAccessException) { _logger.LogWarning("임시 파일 정리 실패(권한). TempFile={TempFile}", Path.GetFileName(path)); }
    }

    /// <summary>저장 루트 기준 상대 경로의 파일이 지금 있는가.</summary>
    /// <param name="storagePath">확인할 상대 경로.</param>
    /// <returns>파일이 존재하면 <see langword="true"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 파일 시스템 자체의 존재 확인에 의존한다(호출과 그 결과를 쓰는 코드 사이에 다른 요청이 파일을 지울 수 있다 — TOCTOU는 호출부의 책임이다).</description></item>
    /// <item><description><b>Memory Allocation:</b> 경로 문자열 계산 외 추가 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 동기 파일 I/O(단일 존재 확인, 관리 표면 전용이라 짧다).</description></item>
    /// </list>
    /// </remarks>
    public bool Exists(string storagePath) => File.Exists(PhysicalPath(storagePath));

    /// <summary><c>.tmp</c> 밑의 임시 파일 전체 경로를 나열한다(청소 잡 전용). 심볼릭 링크인 파일은 건너뛴다.</summary>
    /// <returns><c>.tmp</c> 디렉터리가 없으면 빈 시퀀스, 있으면 그 밑의 심볼릭 링크가 아닌 파일 전체 경로.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 이 메서드는 반복자(iterator) 블록이라 <c>foreach</c>로 실제로 순회하기 전에는 <see cref="Directory.Exists"/> 확인조차 실행되지 않는다(지연 평가) — 열거 도중 다른 요청이 파일을 만들거나 지워도 예외 없이 반영되거나 건너뛴다(.NET 파일 열거의 통상 동작).</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="Directory.EnumerateFiles(string)"/>는 스트리밍 열거자라 전체 목록을 한 번에 메모리에 올리지 않는다. 파일마다 <see cref="FileInfo"/> 1개를 링크 여부 확인용으로 만든다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 파일 시스템 열거. 호출자가 순회하는 시점에 <see cref="Directory.Exists"/>·<see cref="Directory.EnumerateFiles(string)"/>·<see cref="FileInfo.LinkTarget"/> 조회가 일어난다(이 메서드 호출 시점이 아니라).</description></item>
    /// </list>
    /// </remarks>
    public IEnumerable<string> EnumerateTempFiles()
    {
        if (!Directory.Exists(_temp)) yield break;
        foreach (var file in Directory.EnumerateFiles(_temp))
        {
            // 심볼릭 링크는 따라가지 않는다: 링크가 가리키는 실제 위치가 저장 루트 밖일 수 있다(기본 거부). PhysicalPath의 봉쇄 검사는
            // 문자열 접두사 비교라 링크를 해석하지 못하므로, 청소 대상 열거 단계에서 걸러야 한다.
            if (new FileInfo(file).LinkTarget is not null) continue;
            yield return file;
        }
    }

    /// <summary>내용 주소 규칙(<c>{sha[..2]}/{sha}.{확장자}</c>)에 <b>모양이 맞는</b> 파일만 열거한다(청소 잡 전용). 규칙 밖의 것(임시 폴더, 시작 확인 파일, 사람이 둔 파일)과
    /// 심볼릭 링크·정션(버킷 디렉터리·파일 어느 쪽이든)은 청소 대상이 아니다(기본 거부).</summary>
    /// <returns>모양이 맞는 각 파일의 상대 경로와, 파일 이름에서 읽은 SHA-256(버킷 접두사와 일치가 이미 확인된 값).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 이 메서드는 반복자(iterator) 블록이라 <c>foreach</c>로 실제로 순회하기 전에는 어떤 디렉터리 I/O도 하지 않는다 — 순회 도중 다른 요청이 디렉터리 자체를 지우지 않는 한(그러면 <see cref="DirectoryNotFoundException"/>이 날 수 있다 — 이 코드베이스에 저장 루트나 버킷 디렉터리를 지우는 경로는 없다) 예외를 던지지 않고 그 시점의 스냅숏만큼만 본다(.NET 파일 열거의 통상 동작).</description></item>
    /// <item><description><b>검사 순서(코드 그대로):</b> ① 버킷 이름이 2자 소문자 hex인가 → ② 그 디렉터리의 <see cref="DirectoryInfo.LinkTarget"/>이 <see langword="null"/>인가 → ③ 파일 이름 모양(길이·<c>name[64] == '.'</c>·<c>IsLowerHex(sha)</c>·<c>sha.StartsWith(bucket)</c>·확장자가 영숫자) → ④ 그 파일의 <see cref="FileInfo.LinkTarget"/>이 <see langword="null"/>인가.
    /// 즉 링크 검사가 이름 검사보다 먼저는 <b>아니다</b>: 이름이 버킷 모양이 아닌 심볼릭 링크·정션은 ②가 아니라 ①에서 걸러진다(어느 쪽이든 따라가지 않으므로 구멍은 없고, 어느 관문이 막았는지가 다르다).
    /// <see cref="Directory.EnumerateDirectories(string)"/>는 링크도 디렉터리로 열거하므로, 이름이 버킷 모양인 링크를 막는 것은 ②다.
    /// 여기를 통과한 뒤에도 호출부(<c>AttachmentJanitor</c>)가 삭제 직전 DB 재조회로 다시 걸러 삭제 폭을 좁힌다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 디렉터리·파일 이름 문자열 몇 개와 링크 여부 확인용 <see cref="DirectoryInfo"/>/<see cref="FileInfo"/>를 반복마다 할당한다. 전체 목록을 배열로 모으지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 파일 시스템 열거. 호출자가 순회하는 동안 디렉터리·파일 I/O가 일어난다(청소 잡은 백그라운드 실행이라 요청 스레드를 막지 않는다).</description></item>
    /// </list>
    /// </remarks>
    public IEnumerable<(string StoragePath, string Sha256)> EnumerateStoredFiles()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var bucket = Path.GetFileName(directory);
            if (bucket.Length != 2 || !IsLowerHex(bucket)) continue;
            // 심볼릭 링크·정션 버킷은 따라가지 않는다: 링크가 가리키는 실제 위치가 저장 루트 밖일 수 있다(기본 거부).
            if (new DirectoryInfo(directory).LinkTarget is not null) continue;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (name.Length is < 66 or > 70 || name[64] != '.') continue;
                var sha = name[..64];
                var extension = name[65..];
                if (!IsLowerHex(sha) || !sha.StartsWith(bucket, StringComparison.Ordinal) || !extension.All(char.IsAsciiLetterOrDigit)) continue;
                // 파일 자체가 심볼릭 링크여도 같은 이유로 건너뛴다.
                if (new FileInfo(file).LinkTarget is not null) continue;
                yield return ($"{bucket}/{name}", sha);
            }
        }
    }

    // 내부 헬퍼: 상용구 remarks 없이 인라인 주석으로 판단 근거만 남긴다 — 소문자 hex만 허용해 대문자·비-hex를 내용 주소가 아닌 것으로 취급한다(기본 거부).
    private static bool IsLowerHex(string value) => value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

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
            // FileOptions.Asynchronous: 커널 오버랩 I/O로 연다. 이 메서드는 요청 처리 스레드에서 직접 호출되므로
            // (관리 표면이라도) WriteAsync가 스레드 풀 스레드를 동기적으로 막지 않고 진짜 비동기로 완료돼야 한다.
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
