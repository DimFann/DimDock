using System.ComponentModel.DataAnnotations.Schema;

namespace Website.Models;

[Table("PathTags")]
public class PathTag
{
    [Column("path_id")]
    public ulong PathId { get; set; }

    [Column("tag_id")]
    public ulong TagId { get; set; }

    public PathEntry Path { get; set; } = null!;
    public Tag Tag { get; set; } = null!;
}
