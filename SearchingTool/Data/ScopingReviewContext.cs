using Microsoft.EntityFrameworkCore;
using SearchingTool.Models;

namespace SearchingTool.Data
{
    public class ScopingReviewContext : DbContext
    {
        public ScopingReviewContext(DbContextOptions<ScopingReviewContext> options)
            : base(options) { }

        public DbSet<Publication> Publications { get; set; }
        public DbSet<Author> Authors { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<Portal> Portals { get; set; }
        public DbSet<Search> Searches { get; set; }
        public DbSet<Result> Results { get; set; }

        public DbSet<PublicationAuthor> PublicationsAuthors { get; set; }
        public DbSet<PublicationCategory> PublicationsCategories { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<PublicationAuthor>()
                .HasKey(pa => new { pa.PublicationId, pa.AuthorId });

            modelBuilder.Entity<PublicationAuthor>()
                .HasOne(pa => pa.Publication)
                .WithMany(p => p.PublicationsAuthors)
                .HasForeignKey(pa => pa.PublicationId);

            modelBuilder.Entity<PublicationAuthor>()
                .HasOne(pa => pa.Author)
                .WithMany(a => a.PublicationsAuthors)
                .HasForeignKey(pa => pa.AuthorId);

            modelBuilder.Entity<PublicationCategory>()
                .HasKey(pc => new { pc.PublicationId, pc.CategoryId });

            modelBuilder.Entity<PublicationCategory>()
                .HasOne(pc => pc.Publication)
                .WithMany(p => p.PublicationsCategories)
                .HasForeignKey(pc => pc.PublicationId);

            modelBuilder.Entity<PublicationCategory>()
                .HasOne(pc => pc.Category)
                .WithMany(c => c.PublicationsCategories)
                .HasForeignKey(pc => pc.CategoryId);

            modelBuilder.Entity<Result>()
                .HasKey(r => new { r.SearchId, r.PublicationId });

            modelBuilder.Entity<Result>()
                .HasOne(r => r.Search)
                .WithMany(s => s.Results)
                .HasForeignKey(r => r.SearchId);

            modelBuilder.Entity<Result>()
                .HasOne(r => r.Publication)
                .WithMany(p => p.Results)
                .HasForeignKey(r => r.PublicationId);
        }
    }
}
