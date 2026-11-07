using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Website.Models
{
    [Table("Paths")]
    public class PathEntry
    {
        [Key]
        [Column("id")]
        public ulong Id { get; set; }

        [Required]
        [Column("path")]
        [MaxLength(1024)]
        public string Path { get; set; } = string.Empty;

        [Column("parent_path")]
        [MaxLength(1024)]
        public string? ParentPath { get; set; }

        [Column("description")]
        public string? Description { get; set; }

        [Column("type")]
        [MaxLength(32)]
        public string? Type { get; set; }

        [Column("metadata")]
        public string? Metadata { get; set; }

        public ICollection<PathTag> TagLinks { get; set; } = new List<PathTag>();
    }
}
