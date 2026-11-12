# Idle Shutdown

Idle Shutdown automaticky vypíná neaktivní počítače s Windows. Řešení má dvě části:

- **IdleShutdown.Service** běží jako LocalSystem, sleduje zamčené a nepřihlášené relace, přijímá požadavky přes named pipe a provádí vypnutí.
- **IdleShutdown.Agent** běží v interaktivní uživatelské relaci, sleduje nečinnost a prezentace a zobrazuje varovný dialog.

## Chování

- `IdleMinutes` je jediný timeout nečinnosti pro odemčený, zamčený i nepřihlášený stav. Po něm vždy následuje dodatečná ochranná doba `WarningSeconds`.
- Odemčená relace: během `WarningSeconds` se ve všech odemčených lokálních/RDP relacích zobrazí společný popup. Libovolný nový vstup nebo tlačítko v dialogu vypnutí zruší.
- Zamčená relace: stejná doba `WarningSeconds` proběhne bez popupu. Vstup na zamykací obrazovce zruší čekání a spustí celý `IdleMinutes` timeout znovu.
- Žádný přihlášený uživatel: `WarningSeconds` proběhne bez popupu. Pohyb myši nebo stisk klávesy na přihlašovací obrazovce zruší čekání a spustí celý timeout znovu.
- Pokud je povolena údržba, **Windows Update a Chocolatey se spouštějí už při zobrazení varovného okna**, respektive při začátku tiché ochranné doby u zamčeného/nepřihlášeného počítače. Kontrola a instalace aktualizací probíhají souběžně s odpočtem. Vlastní instalace se navzájem koordinují, aby si nekonkurovaly při MSI transakcích.
- Po skončení odpočtu **od verze 2.0.3** místo zamrzlého `00:00` ukáže animované točící se kolečko, běžící indikátor a dobu čekání. Procenta nejsou zobrazována, dokud služba nedokáže předat skutečné procento instalací. Dokud běží údržba, počítač se nevypne. Při návratu uživatele se zruší vypnutí a nezahajují se další instalační kroky; již běžící instalátor dostane prostor bezpečně skončit.
- Po vypršení obou dob a případném dokončení údržby služba odešle nevnucené (`/t 0`, bez `/f`) vypnutí, takže Windows může upozornit na aplikaci s neuloženými daty.
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
  "ConfigVersion": "2.0.5",
  "IdleMinutes": 90,
  "WarningSeconds": 300,
  "CheckIntervalSeconds": 5,
  "PauseWhenFullscreen": true,
  "DryRun": false,
  "MaintenanceEnabled": true,
  "WindowsUpdateEnabled": true,
  "DriverUpdatesEnabled": true,
  "OptionalDriversEnabled": true,
  "ChocolateyEnabled": true,
  "ChocolateySource": null,
  "MaintenanceMaxMinutes": 120,
  "SelfUpdateEnabled": true
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

Stabilní verze zapisuje důležité změny stavu, výsledky aktualizací,
odklady a chyby; podrobnější řádky `DEBUG` se do sestavení zapínají **pouze
u beta/RC verzí**. Nastavení `DryRun` slouží pro simulaci vypnutí, nikoliv
pro zapnutí diagnostiky. Hlavní log `IdleShutdown.log` se automaticky rotuje
po 2 MiB na `.1` a `.2` (celkem přibližně 6 MiB).

Samostatný automatický aktualizátor zapisuje do
`C:\ProgramData\IdleShutdown\SelfUpdate.log`; i tento soubor má
**limit 2 MiB a dvě rotované zálohy** (`SelfUpdate.log.1` a
`SelfUpdate.log.2`). Není nutné jej ručně mazat.

## Verzování a migrace konfigurace

Od verze **2.0.2** obsahuje provozní soubor `config.json` položku
`ConfigVersion`, která odpovídá verzi právě nainstalované aplikace.
Chocolatey při upgradu zkontroluje verzi a automaticky převede staré
konfigurace bez této položky (například ze **1.3.4, 2.0.0 nebo 2.0.1**).
Z původního souboru převezme všechna rozpoznaná, výslovně nastavená pole
a chybějící hodnoty doplní z výchozí konfigurace instalovaného vydání.

**Přímý upgrade 1.3.4 → 2.0.2** tedy ponechá původní `IdleMinutes`,
`WarningSeconds`, `CheckIntervalSeconds`, `PauseWhenFullscreen` a `DryRun`;
nové volby `MaintenanceEnabled`, `OptionalDriversEnabled` a
`SelfUpdateEnabled` se doplní jako `true`. Vlastní Chocolatey zdroj
se při aktualizaci z řady 2.x zachová.

Pokud už existující konfigurace obsahuje `MaintenanceEnabled: false`
nebo jiné výslovné nastavení, migrace jej **nepřepne**. Například při
upgradu 2.0.0 → 2.0.2 zůstanou explicitní `false` zachována,
protože nelze spolehlivě rozpoznat výchozí hodnotu od rozhodnutí správce.

Instalátor před migrací vytvoří jednorázovou zálohu
`C:\ProgramData\IdleShutdown\config.json.pre-2.0.2.bak`
a zapíše novou konfiguraci s `ConfigVersion: "2.0.2"`.
Neplatný, poškozený nebo novější konfigurační soubor nepřepíše.
Při selhání instalace se pokusí vrátit původní konfiguraci.
Parametr `/ResetConfig` i nadále znamená **úplné obnovení výchozích hodnot**,
nikoli jen doplnění chybějících položek.

První aktualizace z řady 1.3.x se provádí externě (např. Tactical RMM
nebo Chocolatey), protože 1.3.x nemá mechanismus samoaktualizace.

## Aktualizace systému a aplikací

Od verze **2.0.1** jsou na nových instalacích `MaintenanceEnabled`, `OptionalDriversEnabled` a `SelfUpdateEnabled` ve výchozím stavu **`true`**. Při upgradu se zachovávají **existující hodnoty**; od verze 2.0.2 se soubor navíc automaticky migruje a dostane verzi. Pokud má některý z přepínačů výslovně `false`, zůstane `false`. Změnu může správce provést parametrem instalace, ruční úpravou konfigurace nebo pomocí `/ResetConfig` (ten ale resetuje i ostatní hodnoty).

Při `MaintenanceEnabled: true` se ihned se začátkem upozornění spustí:

- **Windows Update** – aktualizace systému a podle konfigurace ovladače; volitelné ovladače jsou od verze 2.0.1 ve výchozím stavu povolené. Aktualizace vyžadující interakci uživatele se přeskočí.
- **Chocolatey** – `choco upgrade all` z konfigurací zvoleného zdroje. Balíček `idle-shutdown` je z tohoto běhu úmyslně vynechán, aby služba nepřepisovala sama sebe během instalací.

Po dosažení `MaintenanceMaxMinutes` se přestanou zahajovat další části
údržby, ale již běžící instalátor není násilně ukončen. Aplikace sama
nevyvolává automatický restart; některé aktualizace však mohou vyžadovat
pozdější restart nebo jej vyvolat vlastní instalací. `ChocolateySource: null`
použije nakonfigurované zdroje v Chocolatey.

### Bezpečná samoaktualizace Idle Shutdown

`SelfUpdateEnabled: true` povolí **oddělenou naplánovanou úlohu pod SYSTEM** (u nových instalací od verze 2.0.1 je zapnuta standardně)
s názvem `Idle Shutdown Self Update`. Ta kontroluje novější vydání při
startu Windows a denně ve **3:00**. Kontrolu přeskočí, pokud je někdo
přihlášen (včetně zamčené nebo odpojené RDP relace), probíhají aktualizace
nebo již běží jiný self-updater. Není součástí probíhající údržby.

**Automatická samoaktualizace přijímá výhradně stabilní verze** (např.
`2.0.0`, `2.0.1`), nikoliv `beta`, `alpha` ani `rc`. Neexistuje
konfigurační přepínač, který by automatické instalování bet povoloval.
Beta verze se instalují ručně a mohou se automaticky povýšit na novější
stabilní vydání. Instalátor před výměnou souborů ukončí agenty, zastaví
službu, zálohuje původní soubory a při chybě se pokusí obnovit původní stav.
Spuštěný self-updater zůstává mimo složku v `Program Files`.

Kontrolní příkazy (PowerShell jako správce):

```powershell
Get-ScheduledTask -TaskName "Idle Shutdown Self Update"
Get-ScheduledTaskInfo -TaskName "Idle Shutdown Self Update"
Get-Content "$env:ProgramData\IdleShutdown\SelfUpdate.log" -Tail 80
choco list --limit-output
```

Pokud se `SelfUpdate.log` ještě nevytvořil, nemusí to být chyba: vzniká
při prvním diagnostickém zápisu pomocné úlohy. Automatické vydání na GitHub
**nenahrává balíček do interního Chocolatey repozitáře**; do vlastního feedu
jej musí nejprve publikovat správce.

### Oprava přerušené samoaktualizace od 2.0.4

Při přerušení instalace (například restartem Windows) může Chocolatey
zaevidovat novější verzi, ale v `C:\Program Files\IdleShutdown` stále
zůstanou staré EXE. Od **2.0.4** proto SYSTEM aktualizátor porovnává
evidenci Chocolatey se **skutečnou verzí souborů Agent.exe a Service.exe**,
a u vydání 2.0.2+ i s `ConfigVersion` a verzí aktualizační úlohy.
Při nesouladu automaticky použije opravnou instalaci
`choco upgrade idle-shutdown --force --version=<stable>` při další
bezobslužné kontrole, pouze bez přihlášeného uživatele a probíhající údržby.

Zápis `VERIFIED complete` v `SelfUpdate.log` vyžaduje souhlas **všech**
částí; pokud se verze liší, zaznamená se `INCOMPLETE` a úloha vrátí
nenulový výsledek. Volitelné aktualizace systému s čekajícím restartem
mají přednost, samotná opravná instalace se odloží.

Verze **2.0.2 a 2.0.3 měly chybu** při atomickém ukládání
`config.json` (`File.Replace` s neplatnou záložní cestou), která
mohla způsobit selhání upgradu ještě před dokončením migrace konfigurace.
Oprava 2.0.4 vytváří platný dočasný záložní soubor a má Windows regresní
test skutečné výměny existující konfigurace. Zachovává také trvalou
zálohu `config.json.pre-2.0.4.bak`.

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
Instalace se také registruje do standardního 64bitového Windows seznamu
nainstalovaného softwaru jako `Idle Shutdown`. Tactical RMM a podobné nástroje
tak mohou zobrazit verzi, cestu a vydavatele a použít tichý příkaz
`choco uninstall idle-shutdown -y`. Odinstalace odstraní službu, agenta,
scheduled task i inventární záznam; konfigurace a logy v `ProgramData` zůstávají
záměrně zachované.
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

Pro testovací prostředí s vlastním repozitářem a krátkým odpočtem
(pouze na vyhrazeném PC; `DryRun:false` provede skutečnou údržbu a vypnutí):

```powershell
$repo = "https://choco.cpto.ujep.cz/nuget/choco-internal/"
choco upgrade idle-shutdown -y --version=2.0.1 --source="$repo" `
  --params="'/IdleMinutes:1 /WarningSeconds:30 /CheckIntervalSeconds:5 /PauseWhenFullscreen:true /DryRun:false /MaintenanceEnabled:true /WindowsUpdateEnabled:true /DriverUpdatesEnabled:true /OptionalDriversEnabled:true /ChocolateyEnabled:true /ChocolateySource:$repo /MaintenanceMaxMinutes:30 /SelfUpdateEnabled:true'"
```

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
