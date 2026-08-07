# Idle Shutdown

Idle Shutdown automaticky vypíná neaktivní počítače s Windows. Řešení má dvě části:

- **IdleShutdown.Service** běží jako LocalSystem, sleduje zamčené a nepřihlášené relace, přijímá požadavky přes named pipe a provádí vypnutí.
- **IdleShutdown.Agent** běží v interaktivní uživatelské relaci, sleduje nečinnost a prezentace a zobrazuje varovný dialog.

## Chování

- Odemčená relace: po `IdleMinutes` nečinnosti se zobrazí odpočet `WarningSeconds`. Libovolný nový vstup nebo tlačítko v dialogu vypnutí zruší.
- Zamčená relace: služba vypne počítač po `LockedMinutes` bez popupu. Vstup na zamykací obrazovce timer resetuje a těsně před vypnutím se kontroluje ještě jednou.
- Žádný přihlášený uživatel: služba vypne počítač po `NoUserMinutes` bez popupu.
- `PauseWhenFullscreen`: před varováním se kontrolují systémové power/execution requests a poté skutečný fullscreen foreground okna.
- `DryRun`: při hodnotě `true` služba vypnutí pouze zapíše do logu.

Výchozí produkční konfigurace:

```json
{
  "IdleMinutes": 60,
  "WarningSeconds": 300,
  "LockedMinutes": 60,
  "NoUserMinutes": 60,
  "CheckIntervalSeconds": 5,
  "PauseWhenFullscreen": true,
  "DryRun": false
}
```

Konfigurace a společný log jsou v:

```text
C:\ProgramData\IdleShutdown\config.json
C:\ProgramData\IdleShutdown\IdleShutdown.log
```

## Sestavení

Na macOS nebo Linuxu s .NET 8 SDK spusťte:

```bash
chmod +x ./build.sh
./build.sh
```

Na Windows s .NET 8 SDK spusťte:

```bat
build.bat
```

Oba skripty vytvoří stejné self-contained `win-x64` aplikace a rovnou také
kompletní Chocolatey balíček `dist/package/idle-shutdown.nupkg`. Skript lze
spustit opakovaně; předchozí výstupy i předchozí lokální `.nupkg` nejprve
bezpečně vyčistí.

Aktuální verze projektu je uložena pouze v kořenovém souboru `VERSION`.
Tuto hodnotu automaticky používají assembly služby i agenta a Chocolatey
balíček. Před vydáním nové verze tedy stačí změnit právě tento soubor.

## Lokální instalace

Jako správce spusťte:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-Test.ps1
```

Agent se automaticky spouští při přihlášení. Pro okamžité ruční spuštění:

```powershell
Start-ScheduledTask -TaskName 'Idle Shutdown Agent'
```

Před zrychleným testem doporučujeme nastavit `DryRun` na `true`, upravit timeouty a restartovat službu i agenta:

```powershell
Restart-Service IdleShutdown
Stop-ScheduledTask -TaskName 'Idle Shutdown Agent' -ErrorAction SilentlyContinue
Start-ScheduledTask -TaskName 'Idle Shutdown Agent'
```

## Chocolatey

Hlavní `build.sh` a `build.bat` vytvářejí Chocolatey balíček automaticky.
Pokud už jsou aplikace v `dist` sestavené a chcete znovu vytvořit pouze
balíček, lze na Windows použít:

```powershell
.\Build-ChocolateyPackage.ps1
```

Popis pro ProGet se vždy načítá přímo z `Chocolatey.Description.txt`.
Výsledkem je `dist\package\idle-shutdown.nupkg`; předchozí lokální balíčky
se odstraní. Verze zůstává uložená uvnitř metadat `.nupkg`, takže ji ProGet
správně rozpozná. Pro publikaci nové verze do ProGetu je nutné nejprve zvýšit
číslo v souboru `VERSION`.

Instalace z interního zdroje:

```powershell
choco install idle-shutdown -y --source <INTERNAL_CHOCOLATEY_SOURCE>
```

Příklad testovacích parametrů:

```powershell
choco install idle-shutdown -y `
  --source <INTERNAL_CHOCOLATEY_SOURCE> `
  --params="'/IdleMinutes:1 /WarningSeconds:30 /LockedMinutes:1 /NoUserMinutes:1 /CheckIntervalSeconds:5 /PauseWhenFullscreen:false /DryRun:true'"
```

Existující konfigurace se při upgradu zachová. Parametr `/ResetConfig` ji nejprve nahradí výchozí konfigurací. Kontrola instalace:

```powershell
idle-shutdown-test
```

## Odinstalace

```powershell
.\Uninstall.ps1
```

Odinstalace ponechá konfiguraci a log v `C:\ProgramData\IdleShutdown`, aby se neztratila provozní historie.
