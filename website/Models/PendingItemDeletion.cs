using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Website.Models;

[Table("PendingItemDeletions")]
public class PendingItemDeletion
{
    [Key]
    [Column("id")]
    public ulong Id { get; set; }

    [Required]
    [Column("parent_path")]
    [MaxLength(1024)]
    public string ParentPath { get; set; } = string.Empty;

    [Required]
    [Column("original_name")]
    [MaxLength(255)]
    public string OriginalName { get; set; } = string.Empty;
}
