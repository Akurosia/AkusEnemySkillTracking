<?php
declare(strict_types=1);

$maxBytes = 50 * 1024 * 1024;

function env_value(string $name, string $default = ''): string
{
    $value = getenv($name);
    if ($value !== false) {
        return $value;
    }

    static $dotenv = null;
    if ($dotenv === null) {
        $dotenv = [];
        $path = __DIR__ . '/.env';
        if (is_file($path) && is_readable($path)) {
            foreach (file($path, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES) ?: [] as $line) {
                $line = trim($line);
                if ($line === '' || str_starts_with($line, '#') || !str_contains($line, '=')) {
                    continue;
                }

                [$key, $rawValue] = explode('=', $line, 2);
                $key = trim($key);
                $rawValue = trim($rawValue);
                if ($key === '') {
                    continue;
                }

                if (
                    strlen($rawValue) >= 2
                    && (($rawValue[0] === '"' && $rawValue[-1] === '"') || ($rawValue[0] === "'" && $rawValue[-1] === "'"))
                ) {
                    $rawValue = substr($rawValue, 1, -1);
                }

                $dotenv[$key] = $rawValue;
            }
        }
    }

    return $dotenv[$name] ?? $default;
}

function respond(int $status, array $body): never
{
    http_response_code($status);
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode($body, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    exit;
}

function write_json_file(string $path, mixed $value): void
{
    $json = json_encode($value, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    if ($json === false) {
        throw new RuntimeException('Failed to encode JSON for ' . basename($path));
    }

    $tmp = $path . '.tmp';
    if (file_put_contents($tmp, $json . PHP_EOL, LOCK_EX) === false) {
        throw new RuntimeException('Failed to write ' . basename($tmp));
    }

    if (!rename($tmp, $path)) {
        @unlink($tmp);
        throw new RuntimeException('Failed to replace ' . basename($path));
    }
}

// Optional shared secret. Prefer setting this in the web server environment.
// For simple hosting, you can also place AKUS_UPLOAD_TOKEN=change-me in .env next to this script.
$expectedToken = env_value('AKUS_UPLOAD_TOKEN');
$storageDir = rtrim(env_value('AKUS_UPLOAD_DIR', __DIR__ . '/akus_uploads'), '/\\');

if ($_SERVER['REQUEST_METHOD'] !== 'POST') {
    respond(405, ['ok' => false, 'error' => 'POST required']);
}

if ($expectedToken !== '') {
    $providedToken = $_SERVER['HTTP_X_AKUS_TOKEN'] ?? '';
    if (!hash_equals($expectedToken, $providedToken)) {
        respond(401, ['ok' => false, 'error' => 'Invalid token']);
    }
}

$length = (int)($_SERVER['CONTENT_LENGTH'] ?? 0);
if ($length > $maxBytes) {
    respond(413, ['ok' => false, 'error' => 'Payload too large']);
}

$raw = file_get_contents('php://input');
if ($raw === false || $raw === '') {
    respond(400, ['ok' => false, 'error' => 'Empty request body']);
}

try {
    $payload = json_decode($raw, true, 512, JSON_THROW_ON_ERROR);
} catch (JsonException $e) {
    respond(400, ['ok' => false, 'error' => 'Invalid JSON']);
}

if (!is_array($payload)) {
    respond(400, ['ok' => false, 'error' => 'JSON object required']);
}

$required = ['snapshot', 'logdata', 'new_logdata'];
foreach ($required as $key) {
    if (!array_key_exists($key, $payload)) {
        respond(400, ['ok' => false, 'error' => 'Missing key: ' . $key]);
    }
}

if (!is_dir($storageDir) && !mkdir($storageDir, 0775, true)) {
    respond(500, ['ok' => false, 'error' => 'Could not create storage directory']);
}

try {
    $files = [
        'snapshot' => $storageDir . '/enemy-skill-observations.json',
        'logdata' => $storageDir . '/akus-logdata-shaped.json',
        'new_logdata' => $storageDir . '/akus-logdata-new-shaped.json',
        'last_upload' => $storageDir . '/last-upload.json',
    ];

    write_json_file($files['snapshot'], $payload['snapshot']);
    write_json_file($files['logdata'], $payload['logdata']);
    write_json_file($files['new_logdata'], $payload['new_logdata']);
    write_json_file($files['last_upload'], [
        'plugin' => $payload['plugin'] ?? 'AkusEnemySkillTracking',
        'sent_at_utc' => $payload['sent_at_utc'] ?? null,
        'received_at_utc' => gmdate('c'),
    ]);
} catch (Throwable $e) {
    respond(500, ['ok' => false, 'error' => $e->getMessage()]);
}

respond(200, [
    'ok' => true,
    'stored_at' => gmdate('c'),
    'storage_dir' => $storageDir,
    'files' => array_map('basename', $files ?? []),
]);
