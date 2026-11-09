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
        public DbSet<DirectoryShortcut> DirectoryShortcuts { get; set; }
        public DbSet<Blog> Blogs { get; set; }
        public DbSet<Tag> Tags { get; set; }
        public DbSet<ItemTag> ItemTags { get; set; }
        public DbSet<PathTag> PathTags { get; set; }
        public DbSet<AdminOnlyTagSettings> AdminOnlyTagSettings { get; set; }
        public DbSet<AboutContent> About { get; set; }
        public DbSet<PendingPathDeletion> PendingPathDeletions { get; set; }
        public DbSet<PendingItemDeletion> PendingItemDeletions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Optional: configure table names explicitly if needed
            modelBuilder.Entity<LegacyIdMap>().ToTable("LegacyIdMap");
            modelBuilder.Entity<Item>().ToTable("Items");
            modelBuilder.Entity<PathEntry>().ToTable("Paths");
            modelBuilder.Entity<Shortcut>().ToTable("Shortcuts");
            modelBuilder.Entity<Blog>().ToTable("Blogs");
            modelBuilder.Entity<Tag>().ToTable("Tags");
            modelBuilder.Entity<ItemTag>().ToTable("ItemTags");
            modelBuilder.Entity<PathTag>().ToTable("PathTags");
            modelBuilder.Entity<AdminOnlyTagSettings>().ToTable("AdminOnlyTagSettings");
            modelBuilder.Entity<AboutContent>().ToTable("About");
            modelBuilder.Entity<PendingPathDeletion>().ToTable("PendingPathDeletions");
            modelBuilder.Entity<PendingItemDeletion>().ToTable("PendingItemDeletions");
            modelBuilder.Entity<ItemTag>().HasKey(link => new { link.ItemId, link.TagId });
            modelBuilder.Entity<ItemTag>()
                .HasOne(link => link.Item)
                .WithMany(item => item.TagLinks)
                .HasForeignKey(link => link.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<ItemTag>()
                .HasOne(link => link.Tag)
                .WithMany(tag => tag.ItemLinks)
                .HasForeignKey(link => link.TagId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<PathTag>().HasKey(link => new { link.PathId, link.TagId });
            modelBuilder.Entity<PathTag>()
                .HasOne(link => link.Path)
                .WithMany(path => path.TagLinks)
                .HasForeignKey(link => link.PathId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<PathTag>()
                .HasOne(link => link.Tag)
                .WithMany(tag => tag.PathLinks)
                .HasForeignKey(link => link.TagId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<Tag>()
                .HasIndex(tag => tag.Name)
                .IsUnique();
            modelBuilder.Entity<Shortcut>()
                .HasOne(s => s.OriginalItem)
                .WithMany() // or .WithMany(i => i.Shortcuts) if you add a collection to Item
                .HasForeignKey(s => s.OriginalId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<DirectoryShortcut>()
                .HasOne(shortcut => shortcut.TargetPath)
                .WithMany()
                .HasForeignKey(shortcut => shortcut.TargetPathId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<DirectoryShortcut>()
                .HasOne(shortcut => shortcut.ParentPath)
                .WithMany()
                .HasForeignKey(shortcut => shortcut.ParentPathId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
