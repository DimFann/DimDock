using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Website.Models;

[Table("About")]
public sealed class AboutContent
{
    [Key]
    [Column("id")]
    public ulong Id { get; set; }

    [Column("description")]
    public string? Description { get; set; }
}
