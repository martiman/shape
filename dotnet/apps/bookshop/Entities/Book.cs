using Shape.Attributes;

namespace Bookshop.Entities;

[Entity("books", Writable = true)]
[Access(Read = new[] { "user", "admin" }, Write = new[] { "admin" })]
public sealed class Book
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    [Fk(typeof(Author))]
    public int AuthorId { get; set; }

    [Column(Length = 200)]
    [Validate("notEmpty")]
    public string Title { get; set; } = "";

    [Column(Length = 20)]
    public string? Isbn { get; set; }

    [Validate("nonNegative")]
    public decimal Price { get; set; }

    /// <summary>
    /// Public URL of the cover image. Populated by the <c>bookcovers</c> upload
    /// endpoint — the client posts the file, then echoes the returned URL into
    /// this column via a normal mutation.
    /// </summary>
    [Column(Length = 500)]
    public string? CoverUrl { get; set; }

    /// <summary>Soft-delete marker. Reads filter <c>deleted_at IS NULL</c> automatically.</summary>
    public DateTime? DeletedAt { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? CreatedBy { get; set; }
    public int? UpdatedBy { get; set; }
}
