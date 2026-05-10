@echo off
cd /d "%~dp0"

dotnet build ".\AkusEnemySkillTracking\AkusEnemySkillTracking.csproj" -c Debug -p:Platform=x64 --no-restore

pause
