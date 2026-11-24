<?php

declare(strict_types=1);

require_once __DIR__ . '/../bootstrap.php';

use ShapeRuntime\Http\AppRouter;
use Shape\ShapeApp;
use Shape\ShapeRouter;

/**
 * Three backend modes — same API, same frontend, three approaches:
 *
 *   masks    — classic ShapeRuntime: hand-written SQL masks + validators (97 lines of config)
 *   entities — Shape fromEntities: PHP entity classes with attributes (3 files, ~50 lines)
 *   database — Shape fromDatabase: zero code, DB schema IS the API (15 lines of config)
 *
 * Set via SHAPE_MODE env var or defaults to 'database'.
 */
$mode = getenv('SHAPE_MODE') ?: 'database';
$configDir = __DIR__ . '/../config';

switch ($mode) {
    case 'masks':
        $config = ShapeApp::fromEntities(
            __DIR__ . '/../entities',    // entities dir ignored — no entities, masks take over
            $configDir . '/masks.php'
        );
        ShapeRouter::run($config, 'bookshop');
        break;

    case 'entities':
        $config = ShapeApp::fromEntities(
            __DIR__ . '/../entities',
            $configDir . '/entities.php'
        );
        ShapeRouter::run($config, 'bookshop');
        break;

    case 'database':
        $config = ShapeApp::fromDatabase(
            $configDir . '/database.php'
        );
        ShapeRouter::run($config, 'bookshop');
        break;

    default:
        http_response_code(500);
        header('Content-Type: application/json');
        echo json_encode(['error' => ['code' => 'CONFIG_ERROR', 'message' => "Unknown SHAPE_MODE: {$mode}"]]);
}
