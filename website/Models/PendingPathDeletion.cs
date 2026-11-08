using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Website.Models;

[Table("PendingPathDeletions")]
public class PendingPathDeletion
{
    [Key]
    [Column("path")]
    [MaxLength(1024)]
    public string Path { get; set; } = string.Empty;
}
