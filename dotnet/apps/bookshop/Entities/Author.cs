using Shape.Attributes;

namespace Bookshop.Entities;

[Entity("authors", Writable = true)]
[Access(Read = new[] { "user", "admin" }, Write = new[] { "admin" })]
public sealed class Author
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    [Column(Length = 100)]
    [Validate("notEmpty")]
    public string Name { get; set; } = "";

    [Column(Type = "text")]
    public string? Bio { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
