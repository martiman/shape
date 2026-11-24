<?php

declare(strict_types=1);

/**
 * ShapeRuntime mask style — every mask and validator written by hand.
 * This is the classic ShapeRuntime approach: full control, more code.
 */
return [
    'masks' => [
        // Authors
        'authors.list' => [
            'sql' => 'SELECT id, name, email, bio FROM authors',
            'roles' => ['admin', 'user'],
        ],
        'authors.get' => [
            'sql' => 'SELECT id, name, email, bio FROM authors WHERE id = :id',
            'params' => ['id' => ['type' => 'int', 'required' => true]],
            'roles' => ['admin', 'user'],
        ],
        'authors.getMany' => [
            'sql' => 'SELECT id, name, email, bio FROM authors WHERE id IN (:ids)',
            'params' => ['ids' => ['type' => 'array_int', 'required' => true]],
            'roles' => ['admin', 'user'],
        ],
        'authors' => [
            'sql' => 'SELECT id FROM authors LIMIT 1',
            'roles' => ['admin', 'user'],
            'writable' => true,
        ],

        // Books
        'books.list' => [
            'sql' => 'SELECT id, title, author_id, price, isbn, stock FROM books',
            'roles' => ['admin', 'user'],
        ],
        'books.get' => [
            'sql' => 'SELECT id, title, author_id, price, isbn, stock FROM books WHERE id = :id',
            'params' => ['id' => ['type' => 'int', 'required' => true]],
            'roles' => ['admin', 'user'],
        ],
        'books.getMany' => [
            'sql' => 'SELECT id, title, author_id, price, isbn, stock FROM books WHERE id IN (:ids)',
            'params' => ['ids' => ['type' => 'array_int', 'required' => true]],
            'roles' => ['admin', 'user'],
        ],
        'books.byAuthorId' => [
            'sql' => 'SELECT id, title, author_id, price, isbn, stock FROM books WHERE author_id = :author_id',
            'params' => ['author_id' => ['type' => 'int', 'required' => true]],
            'roles' => ['admin', 'user'],
        ],
        'books' => [
            'sql' => 'SELECT id FROM books LIMIT 1',
            'roles' => ['admin', 'user'],
            'writable' => true,
        ],

        // Reviews
        'reviews.list' => [
            'sql' => 'SELECT id, book_id, reviewer, rating, comment FROM reviews',
            'roles' => ['admin', 'user'],
        ],
        'reviews.get' => [
            'sql' => 'SELECT id, book_id, reviewer, rating, comment FROM reviews WHERE id = :id',
            'params' => ['id' => ['type' => 'int', 'required' => true]],
            'roles' => ['admin', 'user'],
        ],
        'reviews.getMany' => [
            'sql' => 'SELECT id, book_id, reviewer, rating, comment FROM reviews WHERE id IN (:ids)',
            'params' => ['ids' => ['type' => 'array_int', 'required' => true]],
            'roles' => ['admin', 'user'],
        ],
        'reviews.byBookId' => [
            'sql' => 'SELECT id, book_id, reviewer, rating, comment FROM reviews WHERE book_id = :book_id',
            'params' => ['book_id' => ['type' => 'int', 'required' => true]],
            'roles' => ['admin', 'user'],
        ],
        'reviews' => [
            'sql' => 'SELECT id FROM reviews LIMIT 1',
            'roles' => ['admin', 'user'],
            'writable' => true,
        ],
    ],

    'validators' => [
        'books.insert' => [
            'validators' => [
                'title' => static fn($v) => is_string($v) && trim($v) !== '' ? null : 'title is required',
                'price' => static fn($v) => is_numeric($v) && (float)$v > 0 ? null : 'price must be positive',
            ],
        ],
        'books.update' => [
            'validators' => [
                'title' => static fn($v) => is_string($v) && trim($v) !== '' ? null : 'title is required',
                'price' => static fn($v) => is_numeric($v) && (float)$v > 0 ? null : 'price must be positive',
            ],
        ],
        'authors.insert' => [
            'validators' => [
                'name' => static fn($v) => is_string($v) && trim($v) !== '' ? null : 'name is required',
            ],
        ],
        'authors.update' => [
            'validators' => [
                'name' => static fn($v) => is_string($v) && trim($v) !== '' ? null : 'name is required',
            ],
        ],
        'reviews.insert' => [
            'validators' => [
                'reviewer' => static fn($v) => is_string($v) && trim($v) !== '' ? null : 'reviewer is required',
                'rating' => static fn($v) => is_numeric($v) && (int)$v > 0 ? null : 'rating must be positive',
            ],
        ],
        'reviews.update' => [
            'validators' => [
                'reviewer' => static fn($v) => is_string($v) && trim($v) !== '' ? null : 'reviewer is required',
                'rating' => static fn($v) => is_numeric($v) && (int)$v > 0 ? null : 'rating must be positive',
            ],
        ],
    ],
];
