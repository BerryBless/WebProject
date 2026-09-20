using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Domain;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>기술 블로그의 EF Core DbContext.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 요청 스코프당 1개 인스턴스이며 동시 사용 금지.</description></item>
/// <item><description><b>Memory Allocation:</b> 변경 추적기가 로드한 엔티티 그래프를 스코프 종료까지 보유한다. 읽기 전용 조회는 <c>AsNoTracking()</c> + 프로젝션을 쓴다(목록에서 본문 200KB를 끌어오지 않기 위해).</description></item>
/// <item><description><b>Blocking:</b> 모든 I/O는 async API로 Non-blocking. 동기 <c>SaveChanges()</c> 사용 금지(시작 시 <c>Migrate()</c>만 예외).</description></item>
/// </list>
/// 앱 검증과 별개로 핵심 불변식은 DB 제약(CHECK·UNIQUE)으로 한 번 더 막는다.
/// </remarks>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>공개 슬러그 형식(소문자 영숫자를 하이픈으로 연결, 앞뒤/연속 하이픈 금지).</summary>
    public const string SlugPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    /// <summary>슬러그 최대 길이(문자).</summary>
    public const int SlugMax = 100;

    /// <summary>제목 최대 길이(문자).</summary>
    public const int TitleMax = 200;

    /// <summary>요약 최대 길이(문자).</summary>
    public const int SummaryMax = 300;

    /// <summary>본문 마크다운 최대 크기(UTF-8 바이트).</summary>
    public const int ContentMaxBytes = 204_800;

    /// <summary>시리즈 설명 최대 길이(문자).</summary>
    public const int SeriesDescriptionMax = 1000;

    /// <summary>태그 이름 최대 길이(문자).</summary>
    public const int TagMax = 50;

    /// <summary>첨부 표시용 파일 이름 최대 길이(문자).</summary>
    public const int FileNameMax = 255;

    /// <summary>블로그 글 테이블.</summary>
    public DbSet<Post> Posts => Set<Post>();

    /// <summary>연재 시리즈 테이블.</summary>
    public DbSet<Series> Series => Set<Series>();

    /// <summary>태그 테이블.</summary>
    public DbSet<Tag> Tags => Set<Tag>();

    /// <summary>글-태그 다대다 연결 테이블.</summary>
    public DbSet<PostTag> PostTags => Set<PostTag>();

    /// <summary>단일 행 관리 상태 테이블.</summary>
    public DbSet<AdminState> AdminStates => Set<AdminState>();

    /// <summary>업로드된 이미지 첨부 테이블.</summary>
    public DbSet<Attachment> Attachments => Set<Attachment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Post>(e =>
        {
            e.Property(x => x.Slug).HasMaxLength(SlugMax);
            e.Property(x => x.Title).HasMaxLength(TitleMax);
            e.Property(x => x.Summary).HasMaxLength(SummaryMax);
            e.Property(x => x.Version).IsRowVersion(); // Npgsql: uint + IsRowVersion → xmin 시스템 컬럼
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending(true, false);
            e.HasIndex(x => new { x.SeriesId, x.SeriesOrder, x.CreatedAt, x.Id });
            // Restrict: 시리즈 삭제는 앱이 한 트랜잭션에서 SeriesId·SeriesOrder를 함께 비운 뒤 수행한다(SET NULL은 CK_Posts_Series_Pair 위반).
            e.HasOne(x => x.Series).WithMany(s => s.Posts).HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Posts_Slug_Format", $"\"Slug\" ~ '{SlugPattern}'");
                t.HasCheckConstraint("CK_Posts_Title_NotBlank", "length(btrim(\"Title\")) > 0");
                t.HasCheckConstraint("CK_Posts_Content_Size", $"octet_length(\"ContentMarkdown\") <= {ContentMaxBytes}");
                t.HasCheckConstraint("CK_Posts_Series_Pair", "(\"SeriesId\" IS NULL) = (\"SeriesOrder\" IS NULL)");
                t.HasCheckConstraint("CK_Posts_SeriesOrder_Positive", "\"SeriesOrder\" IS NULL OR \"SeriesOrder\" > 0");
            });
        });
        b.Entity<Series>(e =>
        {
            e.Property(x => x.Slug).HasMaxLength(SlugMax);
            e.Property(x => x.Title).HasMaxLength(TitleMax);
            e.Property(x => x.Description).HasMaxLength(SeriesDescriptionMax);
            e.HasIndex(x => x.Slug).IsUnique();
            e.ToTable("Series", t =>
            {
                t.HasCheckConstraint("CK_Series_Slug_Format", $"\"Slug\" ~ '{SlugPattern}'");
                t.HasCheckConstraint("CK_Series_Title_NotBlank", "length(btrim(\"Title\")) > 0");
            });
        });
        b.Entity<Tag>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(TagMax);
            e.Property(x => x.NormalizedName).HasMaxLength(TagMax);
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Tags_Name_NotBlank", "length(btrim(\"Name\")) > 0");
                t.HasCheckConstraint("CK_Tags_Name_NoSlash", "position('/' in \"Name\") = 0");
            });
        });
        b.Entity<PostTag>(e =>
        {
            e.HasKey(x => new { x.PostId, x.TagId });
            e.HasIndex(x => new { x.TagId, x.PostId });
            e.HasOne(x => x.Post).WithMany(p => p.PostTags).HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tag).WithMany(t => t.PostTags).HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AdminState>(e =>
        {
            e.ToTable("AdminState", t => t.HasCheckConstraint("CK_AdminState_Single", $"\"Id\" = {AdminState.SingletonId}"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasData(new AdminState { Id = AdminState.SingletonId, SessionEpoch = 1 });
        });
        b.Entity<Attachment>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(FileNameMax);
            e.Property(x => x.ContentType).HasMaxLength(20);
            e.Property(x => x.StoragePath).HasMaxLength(80);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasIndex(x => x.Sha256).IsUnique();
            e.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending(true, false);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Attachments_Size", "\"SizeBytes\" BETWEEN 1 AND 10485760");
                t.HasCheckConstraint("CK_Attachments_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("CK_Attachments_ContentType", "\"ContentType\" IN ('image/png', 'image/jpeg', 'image/gif', 'image/webp')");
                t.HasCheckConstraint("CK_Attachments_FileName_NotBlank", "length(btrim(\"FileName\")) > 0");
            });
        });
    }
}
