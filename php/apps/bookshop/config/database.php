<?php

declare(strict_types=1);

/**
 * DB-first style — no entity files.
 * The database schema IS the API definition.
 * Only access rules and validation config here.
 */
return [
    'access' => [
        'authors' => ['admin', 'user'],
        'books'   => ['admin', 'user'],
        'reviews' => ['admin', 'user'],
    ],

    'validate' => [
        'books.title'      => 'notEmpty',
        'books.price'      => 'positive',
        'authors.name'     => 'notEmpty',
        'reviews.reviewer' => 'notEmpty',
        'reviews.rating'   => 'positive',
    ],

    'exclude_tables' => ['users', 'refresh_tokens'],
];
