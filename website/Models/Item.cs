using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Website.Models
{
    [Table("Items")]
    public class Item
    {
        [Key]
        [Column("id")]
        public ulong Id { get; set; }

        [Required]
        [Column("original_name")]
        public string OriginalName { get; set; } = string.Empty;

        [Column("parent_path")]
        public string? ParentPath { get; set; }

        [Required]
        [Column("thumbnail_key")]
        [MaxLength(64)]
        public string ThumbnailKey { get; set; } = string.Empty;

        [Column("description")]
        public string? Description { get; set; }

        [Column("uploaded")]
        public DateTime? Uploaded { get; set; }

        [Column("created")]
        public DateTime? Created { get; set; }

        [Column("display_index")]
        public int? DisplayIndex { get; set; }

        [Column("type")]
        public string? Type { get; set; }

        [Column("metadata")]
        public string? Metadata { get; set; }

        [NotMapped]
        public ulong? ShortcutId { get; set; }

        [NotMapped]
        public bool IsImage => string.Equals(Type, "image", StringComparison.OrdinalIgnoreCase);

        [NotMapped]
        public string ThumbnailUrl
        {
            get
            {
                using var metadata = JsonDocument.Parse(Metadata ?? "{}");
                var version = metadata.RootElement.TryGetProperty("thumbnailVersion", out var value)
                    ? value.GetString() ?? "1" : "1";
                return $"/img_db_thumbs/{ThumbnailKey}.png?v={Uri.EscapeDataString(version)}";
            }
        }

        public void RefreshThumbnailVersion()
        {
            var metadata = JsonNode.Parse(Metadata ?? "{}") as JsonObject
                ?? throw new InvalidOperationException("Item metadata must be a JSON object.");
            metadata["thumbnailVersion"] = Guid.NewGuid().ToString("N");
            Metadata = metadata.ToJsonString();
        }

        public ICollection<ItemTag> TagLinks { get; set; } = new List<ItemTag>();
    }
}
