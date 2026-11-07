using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Website.Models;

[Table("AdminOnlyTagSettings")]
public class AdminOnlyTagSettings
{
    [Key]
    [Column("id")]
    public byte Id { get; set; }

    [Required]
    [Column("tags")]
    public string TagsJson { get; set; } = "[]";
}
