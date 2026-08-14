# StarTruckMP / Slipstream — Deployment-Referenz

Diese Doku beschreibt den kompletten Build- und Release-Prozess für Client-Mod, Updater/Installer
und dedizierten Server. Ziel: reproduzierbar, ohne Rätselraten, auch für andere Agenten.

## Repo-Struktur

- **`yelli77/StarTruckMP-builds`** (privat) — der komplette Source-Code (`Client/`, `Server/` ist
  entfernt, `common/`, `dedicated/`, `updater/`, `MainMenu/`, `Plugin.cs`, etc.) plus die
  kompilierten Artefakte (`builds/*.dll.gz`, `updater/release/SlipstreamInstaller.exe`,
  `bootstrap/bepinex-bootstrap.zip`). Remote-Alias auf der VPS: `github-starttruckmp` (eigener
  SSH-Deploy-Key, `/root/.ssh/starttruckmp_deploy_key`).
- **`yelli77/slipstream`** (öffentlich) — der schlanke Auto-Updater-Feed. Enthält NUR
  `version.json`, `builds/`, `bootstrap/bepinex-bootstrap.zip`, `updater/release/SlipstreamInstaller.exe`.
  Kein Source-Code. Push per HTTPS mit Token aus `/root/mcp-server/.env` (`GITHUB_TOKEN`), da kein
  zweiter Deploy-Key möglich ist (ein Public Key kann bei GitHub nur an ein Repo als Deploy-Key
  hängen). Lokale Arbeitskopie liegt üblicherweise unter `/tmp/slipstream-release` — falls nicht
  vorhanden, frisch klonen.
- **VPS-Pfad des Source-Repos:** `/docker/starttruckmp/src` (= `/src` im Build-Container).

**Nach JEDER Änderung: in BEIDE Repos committen und pushen** (Source-Repo zuerst, dann den
öffentlichen Feed mit den kompilierten Artefakten).

## Build-Umgebung

Alles läuft in einem persistenten Docker-Container `starttruckmp-dotnet-build-1`
(`mcr.microsoft.com/dotnet/sdk:6.0`), mit `/docker/starttruckmp/src` nach `/src` gemountet.
Enthält bereits: `dotnet-ildasm`, `ilspycmd` (unter `/root/.dotnet/tools`, PATH manuell ergänzen),
`nsis` (für den Installer). Ausführen z.B. via:

```
docker exec starttruckmp-dotnet-build-1 bash -c "cd /src && dotnet build StarTruckMP.csproj -c Release"
```

## 1. Client-Mod (StarTruckMP.dll)

```bash
docker exec starttruckmp-dotnet-build-1 bash -c "cd /src && dotnet build StarTruckMP.csproj -c Release"

cd /docker/starttruckmp/src
gzip -c bin/Release/net6.0/StarTruckMP.dll > builds/StarTruckMP-custom-build-<N>.dll.gz

cat > version.json << EOF
{
  "build": "custom-build-<N>",
  "url": "https://raw.githubusercontent.com/yelli77/slipstream/main/builds/StarTruckMP-custom-build-<N>.dll.gz"
}
EOF
```

**Build-Nummer `<N>` bei jedem Release hochzählen** (siehe `builds/` Ordner für die höchste
bestehende Nummer). Zusätzlich in `Plugin.cs` bei Bedarf pflegen:
- `customBuildNumber` (String, nur Anzeige/Log) — nicht kritisch, aber sollte grob stimmen.
- `protocolBuildNumber` (int) — **kritisch**, wird an den Server für den Versionscheck gesendet.
  Nur hochzählen, wenn alte Clients wirklich ausgesperrt werden sollen (siehe Abschnitt
  Versionscheck unten).

## 2. Updater/Installer (Slipstream.exe + SlipstreamInstaller.exe)

```bash
docker exec starttruckmp-dotnet-build-1 bash -c \
  "rm -rf /src/updater/bin /src/updater/obj && cd /src/updater && dotnet publish StarTruckMPUpdater.csproj -c Release -r win-x64"
```

**Wichtiger Zusatzschritt — PE-Subsystem patchen:** Cross-Compile von Linux aus kann den Apphost
nicht auf GUI-Subsystem umstellen (bekannte `NETSDK1074`-Warnung), obwohl `OutputType=WinExe`
gesetzt ist. Ohne diesen Patch hostet Windows 11 die Exe in einem leeren Windows-Terminal-Fenster.
**Nach jedem `dotnet publish` erneut ausführen:**

```bash
docker cp starttruckmp-dotnet-build-1:/src/updater/bin/Release/net6.0-windows/win-x64/publish/Slipstream.exe /tmp/Slipstream-patch.exe
python3 /docker/starttruckmp/src/updater/patch-subsystem.py /tmp/Slipstream-patch.exe
docker cp /tmp/Slipstream-patch.exe starttruckmp-dotnet-build-1:/src/updater/bin/Release/net6.0-windows/win-x64/publish/Slipstream.exe
```

Dann den NSIS-Installer packen:

```bash
docker exec starttruckmp-dotnet-build-1 bash -c \
  "rm -f /dist-installer/*.exe && cd /src/updater/installer && makensis setup.nsi"
docker cp starttruckmp-dotnet-build-1:/dist-installer/SlipstreamInstaller.exe /docker/starttruckmp/src/updater/release/SlipstreamInstaller.exe
```

`setup.nsi` liegt unter `updater/installer/setup.nsi`, referenziert den Publish-Output-Pfad
(`bin/Release/net6.0-windows/win-x64/publish`) — bei Pfadänderungen im `.csproj` dort mit
nachziehen.

## 3. Dedizierter Server

Läuft als eigener Docker-Container `startruckmp-dedicated-1` (Port `7777/udp`), Compose-Datei
`docker/docker-compose.server.yml` (Kontext `..` = `/docker/starttruckmp/src`), Netzwerk
`docker_default` (damit er `discord-bridge-bot` erreicht). Env-Vars: `SERVER_PORT`,
`MAX_CLIENTS`, `SERVER_NAME`, `STARTTRUCKMP_BRIDGE_URL`, `MIN_CLIENT_BUILD`.

Neu bauen + deployen nach Code-Änderungen unter `common/` oder `dedicated/`:

```bash
cd /docker/starttruckmp/src/docker
docker compose -f docker-compose.server.yml up -d --build
```

Logs prüfen: `docker logs --tail 30 startruckmp-dedicated-1`.

## 4. Bootstrap-Paket (BepInEx-Ersteinrichtung)

`bootstrap/bepinex-bootstrap.zip` — wird von `Slipstream.exe` bei Frisch-Installationen
heruntergeladen und entpackt. **Enthält KEINE selbst gebaute `BepInEx.cfg`** — ein früherer Versuch,
eine minimale Config vorzukonfigurieren, hat BepInEx komplett am Laden gehindert (vermutlich
Parsing-Problem bei unvollständigen Dateien). BepInEx generiert seine Config beim allerersten Start
selbst; `Slipstream.exe` patcht danach nur noch den `Enabled`-Wert unter `[Logging.Console]` auf
`false` in der bereits existierenden Datei (`EnsureBepInExConsoleDisabled()` in `updater/Program.cs`).
Falls das Paket geändert werden muss: Original-Zip als Basis nehmen, mit `python3 -c "import zipfile; ..."`
gezielt einzelne Dateien ergänzen/ersetzen, NICHT die ganze Struktur neu aufbauen.

## 5. Versionscheck (Client ↔ Server)

Client sendet beim Verbinden `protocolBuildNumber` (aus `Plugin.cs`) an den Server. Server
vergleicht gegen `MIN_CLIENT_BUILD` (Env-Var in `docker-compose.server.yml`, Default im Code:
151). Zu alt oder gar keine Meldung innerhalb 6s → Kick via Riptides `DisconnectClient(id, message)`
mit Klartext-Grund. Um eine neue Mindestversion zu erzwingen: `protocolBuildNumber` in `Plugin.cs`
UND `MIN_CLIENT_BUILD` in `docker-compose.server.yml` synchron hochsetzen, Server neu deployen
(Schritt 3), Client-Build pushen (Schritt 1).

## 6. Nach jedem Release: committen + pushen

```bash
# Source-Repo
cd /docker/starttruckmp/src
git add -A
git commit -m "<Beschreibung>"
git push origin main

# Öffentlicher Release-Feed (slipstream) — nur die tatsächlich veränderten Artefakte kopieren
cp /docker/starttruckmp/src/builds/StarTruckMP-custom-build-<N>.dll.gz /tmp/slipstream-release/builds/
cp /docker/starttruckmp/src/version.json /tmp/slipstream-release/version.json
cp /docker/starttruckmp/src/updater/release/SlipstreamInstaller.exe /tmp/slipstream-release/updater/release/SlipstreamInstaller.exe  # falls geaendert
cp /docker/starttruckmp/src/bootstrap/bepinex-bootstrap.zip /tmp/slipstream-release/bootstrap/bepinex-bootstrap.zip  # falls geaendert

cd /tmp/slipstream-release
git add -A
git commit -m "<Beschreibung>"
TOKEN=$(grep '^GITHUB_TOKEN=' /root/mcp-server/.env | cut -d= -f2-)
git push "https://${TOKEN}@github.com/yelli77/slipstream.git" main:main
```

Verifizieren, dass es live ist:

```bash
curl -s https://raw.githubusercontent.com/yelli77/slipstream/main/version.json
```

## Bekannte Stolperfallen (bereits gelöst, nicht wiederholen)

- **Selbst gebaute `BepInEx.cfg` im Bootstrap-Paket** → verhindert kompletten BepInEx-Start.
  Niemals eine handgeschriebene, unvollständige Config mitliefern.
- **`PublishSingleFile=true`** → self-extracting Exe wirkt wie ein Dropper, wird von AV als
  verdächtig eingestuft. Deaktiviert, Multi-File-Publish + NSIS-Installer stattdessen.
- **PE-Subsystem nach Linux-Cross-Compile** → bleibt auf Console stehen, muss manuell gepatcht
  werden (siehe Abschnitt 2). Nach JEDEM `dotnet publish` erneut nötig.
- **`Selectable`/`Button`-ColorTint-Übergänge** im MainMenu/PauseScreen-UI-Code zeigen aus
  unbekanntem Grund keine sichtbare Wirkung — für Hover-Effekte stattdessen `EventTrigger` mit
  direktem `image.color`-Set verwenden (siehe `MainMenu/OnlineModeToggle.cs`).
- **`UnityEvent.RemoveAllListeners()`** entfernt nur zur Laufzeit hinzugefügte Listener, NICHT im
  Editor/Prefab konfigurierte ("persistente") — beim Klonen von UI-Elementen mit vorhandenen
  Click-Handlern lieber das Original-Skript entfernen und frische Komponenten aufbauen, statt zu
  versuchen, die alte Verdrahtung zu entfernen.
- **Namespace/Klassen-Namenskollisionen** (z.B. `namespace StarTruckMP` + `class StarTruckMP`,
  `namespace StarTruckMP.StarTruckClient` + `class StarTruckClient`) — mit `using X = Y.Z;`
  Aliasen umgehen statt vollqualifiziert zu referenzieren.
