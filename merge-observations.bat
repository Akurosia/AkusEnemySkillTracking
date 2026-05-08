@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "LOGDATA=T:\var\www\ffxiv\extras\json\logdata_de_minified.json"
set "OUT=T:\var\www\ffxiv\extras\json\logdata_de_minified.merged.json"

if "%~1"=="" (
  echo Usage: %~nx0 ^<enemy-skill-observations.json^> [output-json]
  echo.
  echo Example:
  echo   %~nx0 "C:\Users\%USERNAME%\AppData\Roaming\XIVLauncher\pluginConfigs\AkusEnemySkillTracking\enemy-skill-observations.json"
  exit /b 1
)

if not "%~2"=="" (
  set "OUT=%~2"
)

node "%SCRIPT_DIR%tools\merge-observations.js" --observations "%~1" --logdata "%LOGDATA%" --out "%OUT%"
