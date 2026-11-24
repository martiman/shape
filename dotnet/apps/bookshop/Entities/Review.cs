using Shape.Attributes;

namespace Bookshop.Entities;

[Entity("reviews", Writable = true)]
[Access(Read = new[] { "user", "admin" }, Write = new[] { "user", "admin" })]
public sealed class Review
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    [Fk(typeof(Book))]
    public int BookId { get; set; }

    [Validate("positive")]
    public int Rating { get; set; }

    [Column(Type = "text")]
    [Validate("notEmpty")]
    public string Body { get; set; } = "";

    public DateTime? CreatedAt { get; set; }
    public int? CreatedBy { get; set; }
}
