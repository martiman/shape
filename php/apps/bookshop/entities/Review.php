<?php

declare(strict_types=1);

namespace Bookshop\Entity;

use Shape\Attribute\{Entity, Column, Id, AutoIncrement, FK, Validate, Access};

#[Entity('reviews')]
#[Access(read: ['admin', 'user'], write: ['user'])]
class Review
{
    #[Id, AutoIncrement]
    public int $id;

    #[FK(entity: Book::class)]
    public int $book_id;

    #[Column(type: 'varchar', length: 120)]
    #[Validate('notEmpty')]
    public string $reviewer;

    #[Column(type: 'int')]
    #[Validate('positive')]
    public int $rating;

    #[Column(type: 'text')]
    public ?string $comment = null;
}
