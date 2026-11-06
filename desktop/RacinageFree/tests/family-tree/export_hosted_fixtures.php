<?php
// Golden fixtures for the Racinage Free C# tree renderer (native-host/FamilyTree.cs).
// Renders fixture trees with the hosted FamilyTree.php (batch/graph mode) and writes JSON + expected markup.
// The hosted MySQL database is only read (SELECT). Real trees are anonymized before rendering and export.
// Usage: php export_hosted_fixtures.php [hosted-root] [output-dir]
if (PHP_SAPI !== 'cli') exit(1);
$hosted = rtrim($argv[1] ?? 'C:/wamp64/www/fallax_projects/racinage', '/\\');
$output = $argv[2] ?? __DIR__ . '/fixtures';
const FIXTURE_TODAY = '2026-03-15';

// Copies of core/functions.php helpers with a fixed timezone and "today", so output is deterministic.
function racinage_user_timezone(): string { return 'UTC'; }
function racinage_person_date($value, ?DateTimeZone $timezone = null): ?DateTimeImmutable {
    $value = trim((string)$value);
    if (!preg_match('/^\d{4}-\d{2}-\d{2}$/', $value)) return null;
    $date = DateTimeImmutable::createFromFormat('!Y-m-d', $value, $timezone ?? new DateTimeZone('UTC'));
    $errors = DateTimeImmutable::getLastErrors();
    return $date && ($errors === false || ($errors['warning_count'] === 0 && $errors['error_count'] === 0)) ? $date : null;
}
function racinage_person_is_deceased(array $person): bool {
    $deathDate = trim((string)($person['death_date'] ?? ''));
    return (int)($person['is_deceased'] ?? 0) === 1 || ($deathDate !== '' && $deathDate !== '0000-00-00');
}
function racinage_person_birthday_status($birthDate, $deathDate = '', $today = null, bool $isDeceased = false): string {
    if (racinage_person_is_deceased(['is_deceased' => $isDeceased, 'death_date' => $deathDate])) return '';
    $birth = racinage_person_date($birthDate);
    if (!$birth) return '';
    $current = racinage_person_date($today ?? FIXTURE_TODAY);
    $monthDay = $birth->format('m-d');
    if ($monthDay === $current->format('m-d')) return 'today';
    return $monthDay === $current->modify('+1 day')->format('m-d') ? 'tomorrow' : '';
}
function racinage_person_birthday_icon(string $status): string {
    if (!in_array($status, ['today', 'tomorrow'], true)) return '';
    global $translations;
    $key = $status === 'today' ? 'Birthday today' : 'Birthday tomorrow';
    $safeLabel = htmlspecialchars((string)($translations[$key] ?? $key), ENT_QUOTES, 'UTF-8');
    return '<svg class="person_birthday_icon ' . $status . '" viewBox="0 0 20 20" role="img" aria-label="' . $safeLabel . '">'
        . '<title>' . $safeLabel . '</title>'
        . '<path d="M16.557,4.467h-1.64v-0.82c0-0.225-0.183-0.41-0.409-0.41c-0.226,0-0.41,0.185-0.41,0.41v0.82H5.901v-0.82c0-0.225-0.185-0.41-0.41-0.41c-0.226,0-0.41,0.185-0.41,0.41v0.82H3.442c-0.904,0-1.64,0.735-1.64,1.639v9.017c0,0.904,0.736,1.64,1.64,1.64h13.114c0.904,0,1.64-0.735,1.64-1.64V6.106C18.196,5.203,17.461,4.467,16.557,4.467 M17.377,15.123c0,0.453-0.366,0.819-0.82,0.819H3.442c-0.453,0-0.82-0.366-0.82-0.819V8.976h14.754V15.123z M17.377,8.156H2.623V6.106c0-0.453,0.367-0.82,0.82-0.82h1.639v1.23c0,0.225,0.184,0.41,0.41,0.41c0.225,0,0.41-0.185,0.41-0.41v-1.23h8.196v1.23c0,0.225,0.185,0.41,0.41,0.41c0.227,0,0.409-0.185,0.409-0.41v-1.23h1.64c0.454,0,0.82,0.367,0.82,0.82V8.156z"></path>'
        . '</svg>';
}
// Fixture image scheme; the C# check passes the same mapping. Real media URLs are a later phase.
function racinage_default_person_image_url($gender = 'M') {
    return '/media/images/' . (in_array(strtolower((string)$gender), ['f', 'female'], true) ? 'default_female.webp' : 'default_male.webp');
}
function racinage_person_image_url($imageName, $gender = 'M', $size = 'sm') {
    $base = pathinfo(basename((string)$imageName), PATHINFO_FILENAME);
    return $base === '' ? racinage_default_person_image_url($gender) : '/media/images/persons/' . rawurlencode($base . '_sm.webp');
}

// Load LF copies of the working-tree renderer: the hosted repo stores LF, but a Windows checkout
// (core.autocrlf) has CRLF, which would leak "\r\n" into the multi-line markup strings.
$renderer = sys_get_temp_dir() . '/racinage_free_tree_golden_' . getmypid();
if (!is_dir($renderer)) mkdir($renderer, 0700, true);
foreach (['FamilyTree.php', 'tree_graph.php', 'tree_snapshot_cache.php'] as $file)
    file_put_contents("$renderer/$file", str_replace("\r\n", "\n", file_get_contents("$hosted/includes/$file")));
register_shutdown_function(function () use ($renderer) { array_map('unlink', glob("$renderer/*.php")); rmdir($renderer); });
require_once "$renderer/FamilyTree.php";

// The renderer only reads schema probes from $db when a graph is injected; answer them without MySQL.
$schemaProbe = new class {
    public function query($sql) { return new class { public $num_rows = 1; public function fetch_assoc() { return ['Field' => 'probe']; } }; }
};

function render_case(array $fixture, array $case): string {
    global $translations, $hosted, $schemaProbe;
    $translations = require $hosted . '/includes/languages/' . $case['lang'] . '.php';
    $tree = new FamilyTree($schemaProbe, $case['can_edit'], 0, [], $case['partial_relationship_id'], false);
    $set = function (string $name, $value) use ($tree) { $p = new ReflectionProperty(FamilyTree::class, $name); $p->setValue($tree, $value); };
    $set('primaryRelationshipId', $case['primary_relationship_id']);
    $set('graph', new FamilyTreeGraph($fixture['people'], $fixture['unions'], $fixture['links'], $case['primary_relationship_id']));
    return $tree->buildTree($case['root_person_id']);
}

function cases_for(array $fixture, int $root, int $primary, int $partialLimit, array $variants): array {
    $cases = [];
    foreach ($variants as [$lang, $edit])
        $cases[] = ['label' => "$lang " . ($edit ? 'edit' : 'view'), 'lang' => $lang, 'can_edit' => $edit, 'root_person_id' => $root, 'primary_relationship_id' => $primary, 'partial_relationship_id' => 0];
    foreach (array_slice($fixture['unions'], 0, $partialLimit) as $union) {
        foreach ([(int)$union['person1_id'], (int)$union['person2_id']] as $anchor)
            $cases[] = ['label' => "partial {$union['id']} anchor $anchor", 'lang' => 'en', 'can_edit' => true, 'root_person_id' => $anchor, 'primary_relationship_id' => $primary, 'partial_relationship_id' => (int)$union['id']];
    }
    return $cases;
}

function synthetic_fixture(): array {
    $p = function ($id, $first, $last, $gender, $extra = []) {
        return $extra + ['id' => (string)$id, 'long_id' => "syn$id", 'first_name' => $first, 'last_name' => $last, 'gender' => $gender, 'image_name' => null,
            'birth_date' => null, 'death_date' => null, 'is_deceased' => '0', 'visibility' => 'visible', 'sibling_birth_order' => null, 'preferred_relationship_id' => null];
    };
    $people = [
        $p(18, 'Otto', 'Root', 'M', ['is_deceased' => '1', 'birth_date' => '1930-03-15']),
        $p(19, 'Olga', "O'Brien & <Co>", 'F', ['image_name' => 'olga photo.jpg']),
        $p(1, 'Adam', 'Root', 'M'),
        $p(2, 'Élodie', 'Beth', 'F', ['birth_date' => '1962-03-16']),
        $p(3, 'Cara', '', 'F'),
        $p(4, '', 'Dan', 'M', ['death_date' => '2001-02-03']),
        $p(5, 'Eve', 'Root', 'F', ['birth_date' => '1990-03-15', 'sibling_birth_order' => '2']),
        $p(6, 'Finn', 'Root', 'M', ['visibility' => 'hidden']),
        $p(7, 'Gus', 'Root', 'M', ['birth_date' => '1992-03-16', 'sibling_birth_order' => '1']),
        $p(8, 'Hana', 'Root', 'F', ['sibling_birth_order' => '5']),
        $p(9, 'Ivy', 'Beth', null, ['birth_date' => '0000-00-00']),
        $p(10, 'Kai', 'Root', 'M'),
        $p(11, 'Lou', 'Dan', 'F', ['death_date' => '0000-00-00']),
        $p(12, 'Jon', 'Smith', 'M'),
        $p(13, 'Unknown', '', null),
        $p(14, 'Inconnue', '', 'F'),
        $p(15, 'Max', 'Smith', 'M', ['birth_date' => '2010-02-30']),
        $p(16, 'Ned', 'Other', 'M'),
        $p(17, 'Oli', 'Other', 'F', ['image_name' => 'oli.webp']),
        $p(20, 'Zed', 'Root', 'M', ['sibling_birth_order' => '1', 'preferred_relationship_id' => '108']),
        $p(21, 'Yara', 'Pivot', 'F'),
        $p(22, 'Wren', 'First', 'F'),
        $p(23, 'Vic', 'Pivot', 'M'),
    ];
    $u = fn($id, $a, $b, $type, $current = '1', $sibling = null) => ['id' => (string)$id, 'person1_id' => (string)$a, 'person2_id' => (string)$b, 'relationship_type' => $type, 'is_current' => $current, 'sibling_person_id' => $sibling];
    $unions = [
        $u(107, 18, 19, 'spouse'),
        $u(101, 1, 2, 'spouse'),
        $u(102, 1, 3, 'partner'),
        $u(103, 4, 2, 'spouse', '0'),
        $u(104, 12, 5, 'spouse', '1', '5'),
        $u(105, 12, 14, 'partner'),
        $u(106, 3, 16, 'partner'),
        $u(108, 21, 20, 'spouse'),
        $u(109, 20, 22, 'spouse'),
        $u(110, 21, 23, 'partner', '0'),
    ];
    $links = []; $id = 1000;
    foreach ([[1, 18], [1, 19], [20, 18], [20, 19], [6, 1], [6, 2], [5, 1], [5, 2], [7, 1], [7, 2], [8, 1], [9, 2], [10, 1], [10, 3],
        [11, 4], [11, 2], [13, 12], [13, 5], [15, 12], [15, 14], [17, 3], [17, 16], [9, 2]] as [$child, $parent])
        $links[] = ['id' => (string)$id++, 'parent_id' => (string)$parent, 'child_id' => (string)$child];
    return ['name' => 'synthetic', 'people' => $people, 'unions' => $unions, 'links' => $links];
}

function anonymize(array $people): array {
    foreach ($people as &$person) {
        $id = (int)$person['id'];
        $unknown = trim((string)$person['last_name']) === '' && in_array(mb_strtolower(trim((string)$person['first_name'])), ['unknown person', 'unknown', 'personne inconnue', 'inconnu', 'inconnue'], true);
        $person = [
            'id' => (string)$id, 'long_id' => "person$id",
            'first_name' => $unknown ? $person['first_name'] : (trim((string)$person['first_name']) === '' ? $person['first_name'] : "First$id"),
            'last_name' => trim((string)$person['last_name']) === '' ? $person['last_name'] : "Last$id",
            'gender' => $person['gender'], 'image_name' => trim((string)$person['image_name']) === '' ? $person['image_name'] : "photo$id.webp",
            'birth_date' => $person['birth_date'] === null ? null : '1970-01-01',
            'death_date' => $person['death_date'] === null ? null : '1999-12-31',
            'is_deceased' => $person['is_deceased'], 'visibility' => $person['visibility'],
            'sibling_birth_order' => $person['sibling_birth_order'], 'preferred_relationship_id' => $person['preferred_relationship_id'],
        ];
    }
    return $people;
}

mysqli_report(MYSQLI_REPORT_ERROR | MYSQLI_REPORT_STRICT);
$db = new mysqli('localhost', 'root', '', 'racinage_db');
$db->set_charset('utf8mb4');
$fixtures = [];
$synthetic = synthetic_fixture();
$synthetic['cases'] = array_merge(cases_for($synthetic, 18, 0, 10, [['en', true], ['en', false], ['fr', true], ['fr', false]]), [
    ['label' => 'primary partner 102', 'lang' => 'en', 'can_edit' => true, 'root_person_id' => 18, 'primary_relationship_id' => 102, 'partial_relationship_id' => 0],
    ['label' => 'root without parents 1', 'lang' => 'en', 'can_edit' => true, 'root_person_id' => 1, 'primary_relationship_id' => 0, 'partial_relationship_id' => 0],
    ['label' => 'missing root', 'lang' => 'en', 'can_edit' => true, 'root_person_id' => 999, 'primary_relationship_id' => 0, 'partial_relationship_id' => 0],
]);
$fixtures[] = $synthetic;

// Same reads as FamilyTree::loadGraph(), per real tree.
foreach ($db->query("SELECT id, root_person_id, primary_relationship_id FROM family_trees WHERE root_person_id IS NOT NULL ORDER BY id")->fetch_all(MYSQLI_ASSOC) as $tree) {
    $id = (int)$tree['id'];
    $unions = $db->query("SELECT id, person1_id, person2_id, relationship_type, is_current, sibling_person_id FROM relationships WHERE family_tree_id = $id ORDER BY id")->fetch_all(MYSQLI_ASSOC);
    $links = $db->query("SELECT id, parent_id, child_id FROM parent_child WHERE family_tree_id = $id ORDER BY id")->fetch_all(MYSQLI_ASSOC);
    $people = $db->query("SELECT p.id, p.first_name, p.last_name, p.gender, p.image_name, p.birth_date, p.death_date, p.is_deceased, p.visibility,
            fts.sibling_birth_order, fts.preferred_relationship_id FROM persons p
        JOIN (SELECT root_person_id AS id FROM family_trees WHERE id = $id
            UNION SELECT person1_id FROM relationships WHERE family_tree_id = $id
            UNION SELECT person2_id FROM relationships WHERE family_tree_id = $id
            UNION SELECT parent_id FROM parent_child WHERE family_tree_id = $id
            UNION SELECT child_id FROM parent_child WHERE family_tree_id = $id) member ON member.id = p.id
        JOIN family_trees ft ON ft.id = $id AND ft.family_long_id = p.family_long_id
        LEFT JOIN family_tree_person_settings fts ON fts.family_tree_id = $id AND fts.person_id = p.id ORDER BY p.id")->fetch_all(MYSQLI_ASSOC);
    if (!$people) continue;
    $fixture = ['name' => "hosted_tree_$id", 'people' => anonymize($people), 'unions' => $unions, 'links' => $links];
    $fixture['cases'] = cases_for($fixture, (int)$tree['root_person_id'], (int)$tree['primary_relationship_id'], 3, [['en', true], ['fr', false]]);
    $fixtures[] = $fixture;
}

if (!is_dir($output) && !mkdir($output, 0700, true)) throw new RuntimeException('Cannot create the fixture output directory.');
foreach (glob($output . '/*.json') as $old) unlink($old);
$total = 0;
foreach ($fixtures as $fixture) {
    foreach ($fixture['cases'] as &$case) { $case['html'] = render_case($fixture, $case); $total++; }
    unset($case);
    $fixture['today'] = FIXTURE_TODAY;
    file_put_contents("$output/{$fixture['name']}.json", json_encode($fixture, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES));
}
echo count($fixtures) . " fixtures, $total cases written to $output\n";
