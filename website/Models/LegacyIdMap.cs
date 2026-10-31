using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Website.Models
{
    [Table("LegacyIdMap")]
    public class LegacyIdMap
    {
        [Key]
        [Column("legacy_id")]
        public string LegacyId { get; set; } = string.Empty;

        [Column("redirect_id")]
        public long? RedirectId { get; set; }

        [Column("type")]
        [MaxLength(32)]
        public string? Type { get; set; }
    }
}
