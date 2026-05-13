# AkusEnemySkillTracking

Dalamud dev plugin for collecting enemy actions by territory/zone.

## Build

```powershell
dotnet build .\AkusEnemySkillTracking.sln -c Debug -p:Platform=x64
```

The dev plugin folder is:

```text
AkusEnemySkillTracking\bin\x64\Debug
```

Add that folder path in Dalamud under `/xlsettings` -> Experimental -> Dev Plugin Locations, then enable it from `/xlplugins` -> Dev Tools -> Installed Dev Plugins. Current Dalamud versions expect a manifest next to the DLL, so do not add the `.dll` file path directly.

Use this exact path shape:

```text
C:\Users\kamot\Documents\GitHub\AkuLogdata\AkusEnemySkillTracking\bin\x64\Debug
```

Do not use this path shape:

```text
C:\Users\kamot\Documents\GitHub\AkuLogdata\AkusEnemySkillTracking\bin\x64\Debug\AkusEnemySkillTracking.dll
```

## Use

Open the plugin with:

```text
/akust
```

The plugin records enemy actions, local player job action usage, status applications, damage ranges, enemy HP/level/IDs, and BGM changes. It writes:

```text
enemy-skill-observations.json
enemy-skill-observations.jsonl
akus-logdata-shaped.json
akus-logdata-new-shaped.json
```

inside Dalamud's plugin config directory for this plugin. Use `/akust export` or the window's "Save snapshot" button to force a snapshot write.
`enemy-skill-observations.json` is the raw collector snapshot. `akus-logdata-shaped.json` is the first-pass output in the original logdata-style structure. `akus-logdata-new-shaped.json` is the newer `metadata/music/text/combatants` shape. All plugin JSON files are written as UTF-8.

## Merge Into Existing Logdata

After collecting data in-game, merge a snapshot into the existing German logdata JSON:

```powershell
node .\tools\merge-observations.js `
  --observations "C:\path\to\enemy-skill-observations.json" `
  --logdata "T:\var\www\ffxiv\extras\json\logdata_de_minified.json" `
  --out "T:\var\www\ffxiv\extras\json\logdata_de_minified.merged.json"
```

The merge adds missing `zone -> enemy -> skill -> actionHex` entries and enriches enemies with `id`, `base_id`, `bnpc_id`, `model_id`, `level`, `minHP`, and `maxHP` when available. Skill entries receive damage ranges and `add_status` IDs when captured.
Player job observations are merged under the top-level `Klassen_und_Jobs` key, using localized job names such as `Rotmagier`. Music observations are merged under top-level `Musik`.
Rows captured while zone names are still unresolved RSV placeholders are kept by territory ID in the plugin snapshot and skipped by the merge script until a later snapshot can repair them.

## Optional Remote Upload

The plugin can POST saved data to a PHP endpoint instead of relying only on local files. Enable `Remote upload` in the plugin window, enter the endpoint URL, and optionally set a shared token. Disable `Store local files` if you want remote-only saves.

Copy `tools/logdata_api.php` to your web server. If you want token protection, set this environment variable on the server:

```text
AKUS_UPLOAD_TOKEN=your-secret-token
```

For simple hosting, you can also create a `.env` file next to `logdata_api.php`:

```text
AKUS_UPLOAD_TOKEN=your-secret-token
AKUS_UPLOAD_DIR=/absolute/path/to/write/logdata
```

Then use the endpoint URL in the plugin, for example:

```text
https://example.com/logdata_api.php
```

On every snapshot save/autosave, the plugin sends `snapshot`, `logdata`, and `new_logdata`. The PHP endpoint writes them under `akus_uploads/` next to the script unless `AKUS_UPLOAD_DIR` is set. The plugin window shows the server response, including the resolved `storage_dir`.
