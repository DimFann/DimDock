using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Website.Models
{
    [Table("Shortcuts")]
    public class Shortcut
    {
        [Key]
        [Column("id")]
        public ulong Id { get; set; } // Unique identifier (PK)

        [Required]
        [Column("original_id")]
        public ulong OriginalId { get; set; } // Unique identifier of original item.

        [Required]
        [Column("original_name")]
        public string OriginalName { get; set; } = string.Empty; // Original file name

        [Column("parent_path")]
        public string? ParentPath { get; set; } // Original full parent path e.g. /2025/2025-10

        [Column("shortcut_path")]
        public string? ShortcutPath { get; set; } // Location where this item will appear, in addition to actual location.

        [ForeignKey("OriginalId")]
        public Item? OriginalItem { get; set; }
    }
}
