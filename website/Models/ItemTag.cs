using System.ComponentModel.DataAnnotations.Schema;

namespace Website.Models;

[Table("ItemTags")]
public class ItemTag
{
    [Column("item_id")]
    public ulong ItemId { get; set; }

    [Column("tag_id")]
    public ulong TagId { get; set; }

    public Item Item { get; set; } = null!;
    public Tag Tag { get; set; } = null!;
}
