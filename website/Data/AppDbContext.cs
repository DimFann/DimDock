using Microsoft.EntityFrameworkCore;
using Website.Models;

namespace Website.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<LegacyIdMap> LegacyIdMaps { get; set; }
        public DbSet<Item> Items { get; set; }
        public DbSet<PathEntry> Paths { get; set; }
        public DbSet<Shortcut> Shortcuts { get; set; }
        public DbSet<Blog> Blogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Optional: configure table names explicitly if needed
            modelBuilder.Entity<LegacyIdMap>().ToTable("LegacyIdMap");
            modelBuilder.Entity<Item>().ToTable("Items");
            modelBuilder.Entity<PathEntry>().ToTable("Paths");
            modelBuilder.Entity<Shortcut>().ToTable("Shortcuts");
            modelBuilder.Entity<Blog>().ToTable("Blogs");
            modelBuilder.Entity<Shortcut>()
                .HasOne(s => s.OriginalItem)
                .WithMany() // or .WithMany(i => i.Shortcuts) if you add a collection to Item
                .HasForeignKey(s => s.OriginalId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
