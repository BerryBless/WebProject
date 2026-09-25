using Microsoft.EntityFrameworkCore;
namespace Mini.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Tag> Tags => Set<Tag>();
}
public class Post { public int Id { get; set; } public string Title { get; set; } = ""; }
public class Tag { public int Id { get; set; } public string Name { get; set; } = ""; }
