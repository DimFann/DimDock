using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Website.Models;

[Table("Tags")]
public class Tag
{
    [Key]
    [Column("id")]
    public ulong Id { get; set; }

    [Required]
    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public ICollection<ItemTag> ItemLinks { get; set; } = new List<ItemTag>();
    public ICollection<PathTag> PathLinks { get; set; } = new List<PathTag>();
}
