# AkusEnemySkillTracking

Dalamud dev plugin for collecting enemy actions by territory/zone.

## Build

```powershell
dotnet build .\AkusEnemySkillTracking.sln -c Debug -p:Platform=x64
```

The dev DLL is written to:

```text
AkusEnemySkillTracking\bin\x64\Debug\AkusEnemySkillTracking.dll
```

Add that DLL path in Dalamud under `/xlsettings` -> Experimental -> Dev Plugin Locations, then enable it from `/xlplugins` -> Dev Tools -> Installed Dev Plugins.

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
