<?php
declare(strict_types=1);

use PhpCsFixer\Fixer\Basic\NoTrailingCommaInSinglelineFixer;
use PhpCsFixer\Fixer\Import\NoUnusedImportsFixer;
use PhpCsFixer\Fixer\Whitespace\IndentationTypeFixer;
use PhpCsFixer\Fixer\Whitespace\LineEndingFixer;
use PhpCsFixer\Fixer\Whitespace\NoTrailingWhitespaceFixer;
use PhpCsFixer\Fixer\Whitespace\SingleBlankLineAtEofFixer;
use Symplify\EasyCodingStandard\Config\ECSConfig;

// Lean whitelist — preserves the project's compact coding style.
// No prepared sets (psr12 / common / strict pull in 50+ opinionated reformatters).
// Add a rule here only if its absence allows a real defect, not a style preference.
return ECSConfig::configure()
    ->withPaths([
        __DIR__ . '/packages/shape/src',
        __DIR__ . '/packages/shaperuntime/src',
    ])
    ->withRules([
        SingleBlankLineAtEofFixer::class,
        NoTrailingWhitespaceFixer::class,
        LineEndingFixer::class,
        IndentationTypeFixer::class,
        NoUnusedImportsFixer::class,
        NoTrailingCommaInSinglelineFixer::class,
    ]);
