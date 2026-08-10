# Idle Shutdown

Idle Shutdown automaticky vypíná neaktivní počítače s Windows. Řešení má dvě části:

- **IdleShutdown.Service** běží jako LocalSystem, sleduje zamčené a nepřihlášené relace, přijímá požadavky přes named pipe a provádí vypnutí.
- **IdleShutdown.Agent** běží v interaktivní uživatelské relaci, sleduje nečinnost a prezentace a zobrazuje varovný dialog.

## Chování

- `IdleMinutes` je jediný timeout nečinnosti pro odemčený, zamčený i nepřihlášený stav. Po něm vždy následuje dodatečná ochranná doba `WarningSeconds`.
- Odemčená relace: během `WarningSeconds` se ve všech odemčených lokálních/RDP relacích zobrazí společný popup. Libovolný nový vstup nebo tlačítko v dialogu vypnutí zruší.
- Zamčená relace: stejná doba `WarningSeconds` proběhne bez popupu. Vstup na zamykací obrazovce zruší čekání a spustí celý `IdleMinutes` timeout znovu.
- Žádný přihlášený uživatel: `WarningSeconds` proběhne bez popupu. Pohyb myši nebo stisk klávesy na přihlašovací obrazovce zruší čekání a spustí celý timeout znovu.
- Po vypršení obou dob služba odešle nevnucené (`/t 0`, bez `/f`) vypnutí, takže Windows může upozornit na aplikaci s neuloženými daty.
- Pokud je přihlášeno více uživatelů, zamčený timeout se použije pouze tehdy, když jsou zamčené nebo odpojené všechny jejich relace. Jedna aktivní odemčená relace vypnutí zablokuje.
- Agent běží samostatně v každé lokální i RDP relaci. Jakmile první relace dosáhne timeoutu, služba vyhlásí jeden společný deadline a popup se zbývajícím časem převezmou všechny odemčené relace. Nový ověřený vstup nebo tlačítko „pokračovat“ v jedné relaci zruší vypnutí a zavře popup ve všech ostatních relacích. Neočekávané zavření popupu bez ověřené změny vstupu celý `IdleMinutes` timer neresetuje. Pokud odpočty nezačnou přesně současně, služba čeká na ten, který skončí nejpozději. Požadavek jedné neaktivní relace služba odmítne, pokud je jiná odemčená relace stále aktivní.
- Popup si během odpočtu obnovuje nejvyšší `topmost` pozici bez aktivace a bez opakovaného kradení focusu, takže jej nepřekryje Explorer, instalátor ani updater.
- Aktivní foreground instalátor nebo updater se bere jako chráněná aktivita podobně jako prezentace; popup se ukáže až poté, co instalační dialog přestane být aktivní.
- `PauseWhenFullscreen`: před varováním se kontroluje prezentační `DISPLAY` power request a poté skutečný fullscreen foreground okna. Obecný `SYSTEM` request (například RDP `Legacy Kernel Caller`) sám o sobě vypnutí neblokuje.
- Bezprostředně před každým vypnutím služba kontroluje prezentační `DISPLAY` power request, aktivní instalaci Windows Update a aktivní MSI transakci. Dokud chráněná aktivita trvá, požadavek odloží a kontrolu opakuje; nový fyzický vstup mezitím požadavek zruší. `PauseWhenFullscreen: false` vypne pouze prezentační ochranu, nikoli ochranu Windows Update a MSI. Samotný stav „čeká se na restart“ vypnutí neblokuje, aby Windows mohl aktualizaci dokončit.
- `DryRun`: při hodnotě `true` služba vypnutí pouze zapíše do logu.
- Chybějící, neúplný nebo neplatný `config.json` vypínání bezpečně zastaví. Služba zapíše nejvýše jednu chybu za hodinu a pokračuje automaticky po opravě konfigurace.
- Popup automaticky používá jazyk Windows (`cs`, `en`, `de`, `es`; ostatní jazyky použijí angličtinu).
- Popup automaticky používá světlý nebo tmavý režim podle nastavení aplikací ve Windows.
- Popup zobrazuje nenápadné číslo právě běžící verze v pravém dolním rohu.
- Provozní log služby i agenta je vždy v angličtině bez ohledu na jazyk popupu.

Výchozí produkční konfigurace:

```json
{
  "IdleMinutes": 90,
  "WarningSeconds": 300,
  "CheckIntervalSeconds": 5,
  "PauseWhenFullscreen": true,
  "DryRun": false
}
```

Jediným zdrojem výchozí konfigurace je kořenový soubor `config.json`.
Build nevytváří druhou konfigurační kopii v `dist`; při balení se tento soubor
vloží dovnitř `.nupkg` a instalátor z něj načte výchozí hodnoty. Soubor v
`C:\ProgramData\IdleShutdown` je až provozní konfigurace konkrétní instalace.

Konfigurace a společný log jsou v:

```text
C:\ProgramData\IdleShutdown\config.json
C:\ProgramData\IdleShutdown\IdleShutdown.log
```

V produkčním režimu se zapisují jen důležité změny stavu, odklady, chyby a
provedené vypnutí. Podrobné desetisekundové diagnostiky jsou zapnuté pouze při
`DryRun: true`. Log se po dosažení 2 MB rotuje na `IdleShutdown.log.1` a
`IdleShutdown.log.2`; celková maximální velikost je přibližně 6 MB.

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
Hotové změny jednotlivých verzí jsou vedené v `CHANGELOG.md`.

Po pushnutí změny souboru `VERSION` do větve `main` spustí GitHub Actions
automaticky testy a kompletní Windows build. Workflow ověří shodu verze v
assembly i `.nupkg`, vytvoří tag `v<verze>` a GitHub Release s balíčkem
`idle-shutdown.nupkg`. Release notes obsahují commity od předchozího release
tagu. Push bez změny `VERSION` nový release nevytvoří.

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
  --params="'/IdleMinutes:1 /WarningSeconds:30 /CheckIntervalSeconds:5 /PauseWhenFullscreen:false /DryRun:true'"
```

Existující konfigurace se při upgradu zachová. Parametr `/ResetConfig` ji nejprve nahradí výchozí konfigurací. Kontrola instalace:

```powershell
idle-shutdown-test
```

Pro ověření stavů doporučujeme nejprve použít `DryRun:true` a krátké timeouty:

- po startu bez přihlášení musí timeout vyhodnotit stav `no logged-on user`;
- po přihlášení a zamčení musí pohyb myši nebo stisk klávesy resetovat lock timer;
- po odhlášení se lock timer zruší a začne nový společný `IdleMinutes` timer.

Agent při zamčení kontroluje session-specific input každých 250 ms a posílá
službě pouze tiché resety timeru. Služba navíc spouští skrytý monitor přímo ve
fyzické konzolové relaci; ten pokrývá i přihlašovací obrazovku po restartu nebo
odhlášení, kde WTS na některých Windows neposkytuje `LastInputTime`. Před
vypnutím se znovu ověří vstup i přihlášení uživatele a následuje zrušitelná
ochranná doba `WarningSeconds`. Jednotlivé pohyby a stisky se do logu nezapisují;
v režimu `DryRun` je v diagnostice pouze souhrnný čítač `helperResets`.

Po vypršení popupu se při `DryRun` další popup neukáže, dokud aplikace
nezaznamená nový fyzický vstup. Tím se při krátkých testovacích intervalech
neopakují varování každých několik desítek sekund.

## Odinstalace

```powershell
.\Uninstall.ps1
```

Odinstalace ponechá konfiguraci a log v `C:\ProgramData\IdleShutdown`, aby se neztratila provozní historie.
