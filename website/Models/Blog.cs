using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Website.Models;

[Table("Blogs")]
public class Blog
{
    [Key]
    [Column("id")]
    public ulong Id { get; set; }

    [Column("title")]
    public string? Title { get; set; }

    [Column("alias")]
    public string Alias { get; set; } = string.Empty;

    [Column("source_filename")]
    public string SourceFilename { get; set; } = string.Empty;

    [Column("timestamp")]
    public DateTime? Timestamp { get; set; }

    [Column("text")]
    public string? Text { get; set; }
}
