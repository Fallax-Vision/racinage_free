<?php
// Refreshes the vendored hosted tree front end for Racinage Free. Reads the hosted working tree only.
// Usage: php vendor-hosted-tree.php [hosted-root]
// Writes: tree/assets/*.js (unchanged copies), tree/assets/tree.css (tree rules purged from the hosted
// compiled style.css), tree/assets/i18n.json (EN/FR strings the tree uses), tree/assets/*.webp, VENDORED.md.
if (PHP_SAPI !== 'cli') exit(1);
$hosted = rtrim($argv[1] ?? 'C:/wamp64/www/fallax_projects/racinage', '/\\');
$here = __DIR__;
$assets = "$here/assets";
if (!is_dir($assets)) mkdir($assets, 0700, true);
$read = static fn(string $path): string => str_replace("\r\n", "\n", (string)file_get_contents($path));

$js = ['family_tree_script.js', 'tree_loading.js', 'tree_print.js', 'profile_crop.js', 'person_profile_photo.js'];
foreach ($js as $file) file_put_contents("$assets/$file", $read("$hosted/includes/js/$file"));
foreach (['default_male.webp', 'default_female.webp'] as $file) copy("$hosted/media/images/$file", "$assets/$file");

// Strings: every literal in the hosted tree sources that is a key of the hosted EN language file.
$sources = ['pages/trees.php', 'includes/tree_person_modal.include.php', 'includes/tree_save.include.php', 'includes/tree_relationship_actions.php',
    'includes/tree_partial_functions.php', 'includes/tree_partial_transfer_functions.php', 'includes/tree_insights_functions.php',
    'includes/tree_important_dates_functions.php', 'includes/collections_tree_rows.php', 'includes/collections_tree_picker.php',
    'includes/person_gallery.include.php', 'includes/person_gallery_functions.php', 'includes/persons_functions.php', 'includes/FamilyTree.php',
    'includes/js/family_tree_script.js', 'includes/js/profile_crop.js', 'includes/js/person_profile_photo.js', 'includes/js/tree_print.js'];
$en = require "$hosted/includes/languages/en.php";
$fr = require "$hosted/includes/languages/fr.php";
$keys = [];
// The Free page template and C# endpoints are scanned too, so their messages get the hosted translations.
$sourcePaths = array_merge(array_map(fn($f) => "$hosted/$f", $sources), glob("$here/../native-host/FamilyTree*.cs"), glob("$here/*.html"), glob("$here/*.js"));
foreach ($sourcePaths as $source) {
    $text = $read($source);
    preg_match_all('/\{\{t:([^}]+)\}\}/', $text, $templateKeys);
    foreach ($templateKeys[1] as $key) if (isset($en[$key]) || isset($fr[$key])) $keys[$key] = true;
    // Quote pairing is unreliable across PHP, JS and C# (HTML attributes, apostrophes), so each known key is
    // looked up as a quoted literal instead.
    foreach (array_keys($en + $fr) as $key) {
        $key = (string)$key;
        if ($key !== '' && (str_contains($text, "'" . str_replace("'", "\\'", $key) . "'") || str_contains($text, '"' . str_replace('"', '\\"', $key) . '"'))) $keys[$key] = true;
    }
}
foreach (['Birthday', 'Death anniversary', 'Page %s of %s', 'Pagination', 'Family Tree', 'Manage Persons', 'Trees'] as $key) if (isset($en[$key])) $keys[$key] = true;
ksort($keys, SORT_STRING);
$i18n = ['en' => [], 'fr' => []];
foreach (array_keys($keys) as $key) {
    $i18n['en'][$key] = (string)($en[$key] ?? $key);
    $i18n['fr'][$key] = (string)($fr[$key] ?? $en[$key] ?? $key);
}
// Free-only wording (2-tree cap, offline messages) is kept next to this script.
foreach (json_decode((string)@file_get_contents("$here/i18n_free.json"), true) ?: [] as $lang => $strings) {
    foreach ($strings as $key => $value) $i18n[$lang][$key] = $value;
}
foreach (array_keys($i18n['fr']) as $key) $i18n['en'][$key] ??= $key;
file_put_contents("$assets/i18n.json", json_encode($i18n, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES));

// CSS: keep hosted rules whose selectors only use classes, ids and data attributes that appear in the
// tree front end (vendored JS, the C# page and renderer). Element-only selectors are dropped so the
// hosted global styles never restyle the Free shell.
$known = [];
$harvest = array_merge(array_map(fn($f) => "$assets/$f", $js), glob("$here/../native-host/FamilyTree*.cs"), glob("$here/*.css"), glob("$here/*.js"), glob("$here/*.html"));
foreach ($harvest as $file) {
    preg_match_all('/[A-Za-z_][A-Za-z0-9_-]*/', $read($file), $words);
    foreach ($words[0] as $word) $known[$word] = true;
}
function tree_css_blocks(string $css): array {
    $blocks = []; $len = strlen($css); $start = 0; $depth = 0; $prelude = ''; $bodyStart = 0;
    for ($i = 0; $i < $len; $i++) {
        $c = $css[$i];
        if ($c === '/' && ($css[$i + 1] ?? '') === '*') { $end = strpos($css, '*/', $i + 2); $i = $end === false ? $len : $end + 1; continue; }
        if ($c === '"' || $c === "'") { for ($i++; $i < $len && $css[$i] !== $c; $i++) if ($css[$i] === '\\') $i++; continue; }
        if ($c === '{') { if ($depth++ === 0) { $prelude = trim(substr($css, $start, $i - $start)); $bodyStart = $i + 1; } }
        elseif ($c === '}') { if (--$depth === 0) { $blocks[] = [$prelude, substr($css, $bodyStart, $i - $bodyStart)]; $start = $i + 1; } }
        elseif ($c === ';' && $depth === 0) $start = $i + 1;
    }
    return $blocks;
}
function tree_css_keep_selector(string $selector, array $known): bool {
    if (trim($selector) === ':root') return true;
    preg_match_all('/[.#]([A-Za-z_][A-Za-z0-9_-]*)|\[([A-Za-z_][A-Za-z0-9_-]*)/', preg_replace('/:not\([^)]*\)/', '', $selector), $tokens, PREG_SET_ORDER);
    if (!$tokens) return false;
    static $prefixes = null;
    $prefixes ??= array_values(array_filter(array_keys($known), fn($w) => strlen($w) >= 5 && in_array(substr($w, -1), ['_', '-'], true)));
    foreach ($tokens as $token) {
        $name = $token[1] !== '' ? $token[1] : $token[2];
        if (isset($known[$name])) continue;
        // Classes built at runtime from a harvested prefix, such as `tree_insights_tint_${color}`.
        $prefixed = false;
        foreach ($prefixes as $prefix) if (str_starts_with($name, $prefix)) { $prefixed = true; break; }
        if (!$prefixed) return false;
    }
    return true;
}
function tree_css_purge(string $css, array $known, array &$keyframes): string {
    $out = '';
    foreach (tree_css_blocks($css) as [$prelude, $body]) {
        $prelude = preg_replace('/\s+/', ' ', preg_replace('#/\*.*?\*/#s', '', $prelude));
        if (preg_match('/^@(media|supports|container)\b/i', $prelude)) {
            $inner = tree_css_purge($body, $known, $keyframes);
            if ($inner !== '') $out .= "$prelude{{$inner}}";
        } elseif (preg_match('/^@(-webkit-)?keyframes\s+([\w-]+)/i', $prelude, $match)) {
            $keyframes[$match[2]] = "$prelude{" . tree_css_min($body) . '}';
        } elseif ($prelude !== '' && $prelude[0] !== '@') {
            // Split on top-level commas only, so lists inside :is(), :not() and :has() stay intact.
            $selectors = array_filter(array_map('trim', preg_split('/,(?![^()]*\))/', $prelude)), fn($s) => tree_css_keep_selector($s, $known));
            if ($selectors) $out .= implode(',', $selectors) . '{' . tree_css_min($body) . '}';
        }
    }
    return $out;
}
function tree_css_min(string $css): string {
    $css = preg_replace('#/\*.*?\*/#s', '', $css);
    $css = preg_replace('/\s+/', ' ', $css);
    $css = preg_replace('/\s*([{};:,>])\s*/', '$1', $css);
    return trim(str_replace(';}', '}', $css), " ;");
}
$keyframes = [];
$css = tree_css_purge($read("$hosted/includes/styles/style.css"), $known, $keyframes);
foreach ($keyframes as $name => $rule) if (str_contains($css, $name)) $css .= $rule;
$css = str_replace('../../media/', '/tree-assets/', $css);
file_put_contents("$assets/tree.css", $css);

$commit = trim((string)shell_exec('git -C ' . escapeshellarg($hosted) . ' rev-parse HEAD'));
$dirty = trim((string)shell_exec('git -C ' . escapeshellarg($hosted) . ' status --porcelain -- includes pages'));
$rows = [];
foreach (array_merge(array_map(fn($f) => "includes/js/$f", $js), ['includes/styles/style.css (purged to tree.css)', 'includes/languages/en.php, fr.php (i18n.json)', 'media/images/default_male.webp', 'media/images/default_female.webp']) as $file) {
    $path = "$hosted/" . preg_replace('/ \(.*$/', '', explode(',', $file)[0]);
    $rows[] = '| `' . $file . '` | `' . substr(hash('sha256', $read($path)), 0, 16) . '` |';
}
$out = "# Vendored hosted tree front end\n\nGenerated by `vendor-hosted-tree.php`. Do not edit the files in `assets/` by hand; refresh them from the hosted repository instead.\n\n"
    . "- Hosted commit: `$commit`\n- Hosted working tree: " . ($dirty === '' ? 'clean' : 'uncommitted changes under includes/ and pages/ were included') . "\n- Generated: " . gmdate('Y-m-d H:i') . " UTC\n\n"
    . "| Hosted source | SHA-256 (first 16, LF) |\n|---|---|\n" . implode("\n", $rows) . "\n\n"
    . "Free-only files next to this manifest: `tree-adapter.js` and `tree-adapter.css` (shell glue, hosted-only features hidden).\n"
    . "Server side: `native-host/FamilyTree.cs` (renderer, golden tested) and `native-host/FamilyTreeApi.cs` (tree_save, person_gallery and persons endpoints).\n";
file_put_contents("$here/VENDORED.md", $out);
printf("Vendored %d JS files, %d strings, tree.css %d bytes.\n", count($js), count($keys), strlen($css));
