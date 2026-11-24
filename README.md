# Fan Control

Jednoduchá aplikace pro Windows na ovládání rychlosti větráků – grafické karty i větráků na základní desce (CPU, skříň).

- **Tři režimy pro každý větrák**
  - **Automaticky** – řízení nechá na ovladači grafické karty / BIOSu.
  - **Ručně** – pevná rychlost posuvníkem.
  - **Podle teploty** – vlastní křivka teplota → otáčky. Body se tahají myší, dvojklik přidá bod, pravé tlačítko ho smaže. Připravené předvolby *Tichý*, *Vyvážený* a *Výkonný*.
    Křivka může sledovat **víc teplot najednou** (např. GPU i CPU) – větrák se pak řídí podle nejvyšší z nich.
- **Profily** – každý profil má vlastní nastavení všech větráků (výchozí *Normální*, *Tichý* a *Výkonný*, další jde přidat, přejmenovat i smazat). Přepínají se v okně nebo jedním kliknutím z ikony vedle hodin.
- **Přehled senzorů** – vše, co hardware nabízí: teploty, otáčky, vytížení, frekvence, příkon, napětí, paměť, disky i síť, včetně minima a maxima a s vyhledáváním. Na hlavní stránce dlaždice s teplotou CPU a GPU, vytížením procesoru a pamětí.
- **Běží na pozadí** v oznamovací oblasti vedle hodin; volitelně se spouští po přihlášení do Windows (bez dotazu UAC).
- **Bezpečnost** – při ukončení, pádu aplikace nebo nedostupném senzoru teploty (kterémkoli z vybraných) se větráky vždy vrátí pod řízení systému. Změny otáček jsou plynulé a hystereze brání neustálému kolísání.
- Moderní vzhled Windows 11, světlý i tmavý motiv.

## Instalace

1. Stáhni `FanControl.exe` ze stránky [Releases](https://github.com/Quee88/Fan-control/releases) (jediný soubor, nic dalšího se instalovat nemusí).
2. Spusť ho – Windows se zeptá na oprávnění správce (bez nich nejde k větrákům přistupovat).
3. Pro teplotu procesoru a větráky na základní desce je potřeba ovladač [PawnIO](https://pawnio.eu/). Pokud chybí nebo je nainstalovaný jen napůl (ovladač bez zařízení – teploty CPU pak zůstávají prázdné), aplikace ho nabídne nainstalovat či opravit jedním tlačítkem. Grafické karty NVIDIA/AMD ho nepotřebují.

## Podporovaný hardware

Aplikace stojí na knihovně [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor):

| Hardware | Čtení | Ovládání |
|---|---|---|
| Grafické karty NVIDIA a AMD | ✅ | ✅ (pokud karta má větrák řízený ovladačem) |
| Běžné základní desky (ASUS, MSI, Gigabyte, ASRock…) | ✅ | ✅ (čipy Nuvoton, ITE, Fintek) |
| Značkové PC a notebooky (HP, Dell, Lenovo…) | většinou ✅ | ⚠️ často ne – větráky řídí firmware výrobce, rychlost jde změnit jen v BIOSu |

## Ovládání z příkazové řádky

| Příkaz | Co udělá |
|---|---|
| `FanControl.exe --exit` | Korektně ukončí běžící aplikaci (větráky vrátí systému). |
| `FanControl.exe --autostart` | Spuštění po přihlášení; s volbou „neotevírat okno“ zůstane jen v oznamovací oblasti. |

Nastavení a log jsou v `%AppData%\Fan-control`.

## Sestavení ze zdrojáků

Potřebuješ [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
# vývojová verze (spouští se bez UAC, takže bez přístupu k některému hardwaru)
dotnet run --project src/FanControl

# výsledný jeden .exe soubor -> publish\FanControl.exe
dotnet publish src/FanControl -c Release -o publish
```

Struktura:

```
src/FanControl/
  Services/HardwareService.cs   – čtení senzorů a řízení větráků (vlákno na pozadí)
  Services/StartupService.cs    – spouštění s Windows přes Plánovač úloh
  Controls/CurveEditor.cs       – interaktivní graf křivky
  ViewModels/                   – logika obrazovek
  MainWindow.xaml               – uživatelské rozhraní
tools/make-icon.ps1             – vygeneruje ikonu aplikace
```

## Licence

Zatím bez licence. Knihovna LibreHardwareMonitor je pod licencí MPL 2.0.
