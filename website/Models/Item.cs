using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Website.Models
{
    [Table("Items")]
    public class Item
    {
        [Key]
        [Column("id")]
        public ulong Id { get; set; }

        [Required]
        [Column("original_name")]
        public string OriginalName { get; set; } = string.Empty;

        [Column("parent_path")]
        public string? ParentPath { get; set; }

        [Required]
        [Column("thumbnail_key")]
        [MaxLength(64)]
        public string ThumbnailKey { get; set; } = string.Empty;

        [Column("description")]
        public string? Description { get; set; }

        [Column("uploaded")]
        public DateTime? Uploaded { get; set; }

        [Column("created")]
        public DateTime? Created { get; set; }

        [Column("display_index")]
        public int? DisplayIndex { get; set; }

        [Column("type")]
        public string? Type { get; set; }

        [Column("metadata")]
        public string? Metadata { get; set; }

        public ICollection<ItemTag> TagLinks { get; set; } = new List<ItemTag>();
    }
}
