using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Website.Models;

[Table("DirectoryShortcuts")]
public class DirectoryShortcut
{
    [Key]
    [Column("id")]
    public ulong Id { get; set; }

    [Column("target_path_id")]
    public ulong TargetPathId { get; set; }

    [Column("parent_path_id")]
    public ulong ParentPathId { get; set; }

    [Required]
    [MaxLength(255)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    public PathEntry TargetPath { get; set; } = null!;
    public PathEntry ParentPath { get; set; } = null!;
}
