# Feature-Plan: Changelog-Assistent (lokal, mit Claude)

> Status: **geplant, noch nicht begonnen.** Startpunkt für eine neue Sitzung – vor dem Start die
> [offenen Fragen](#offene-fragen) klären. Verwandtes Feature: [Backport-Assistent](backport-assistant.md)
> (gemeinsamer Baustein `ClaudeRunner`, siehe [unten](#gemeinsame-bausteine-mit-dem-backport-assistenten)).

## Ziel

Für ein Mod-Projekt (= eine Minecraft-Version / ein Worktree) einen Changelog für die **aktuelle Mod-Version**
erzeugen. Claude fasst die Commits seit der letzten Version zu einem lesbaren Changelog zusammen.

Dateiregeln:

- **`CHANGELOG.md`** enthält immer nur die **aktuelle** Version.
- Steht in `CHANGELOG.md` bereits eine **andere (ältere) Version**, wird deren Inhalt **oben** in
  **`CHANGELOG_FULL.md`** eingefügt, bevor `CHANGELOG.md` mit der neuen Version überschrieben wird.
- `CHANGELOG_FULL.md` ist damit die vollständige Historie, neueste Version zuerst.

Anforderungen (wie beim Backport-Assistenten):

- **Alles läuft lokal** (Claude Code CLI auf dem Rechner, kein CI).
- **Keine Commits durch das Feature.** Es schreibt nur die beiden Dateien; der Nutzer prüft und committet selbst
  (z. B. über den vorhandenen Commit-Dialog).

## Ablauf

1. **Version bestimmen:** `mod_version` (bzw. `version`) aus `gradle.properties` des Projekts – liegt schon als
   `ModProject.modVersion()` vor; dazu `ModProject.mcVersion()` / `gradleMcVersion()`.
2. **Commit-Bereich bestimmen** (Start = „letzte Version“, Ende = `HEAD` des Worktrees), Reihenfolge der Quellen:
   1. Git-Tag der vorherigen Version (Schema klären, siehe offene Fragen),
   2. der Commit, der `mod_version` in `gradle.properties` zuletzt geändert hat,
   3. der Commit, der `CHANGELOG.md` zuletzt geändert hat,
   4. manuell im Dialog wählbar (Start-Commit).
3. **Commits sammeln:** `git log <start>..HEAD` mit SHA, Betreff, Text, Autor, Datum; gruppiert nach
   Conventional-Commit-Typ. Backport-Commits mit `(cherry picked from commit …)` bleiben erhalten (Hinweis „backport“).
   Merge-Commits und reine Versions-/CI-Commits werden standardmäßig ausgeblendet.
4. **Entwurf erzeugen:**
   - **Mit Claude:** Commit-Liste + Vorlage + Regeln (siehe unten) → Claude liefert den Markdown-Text auf stdout.
   - **Ohne Claude (Fallback):** deterministisch nach Typ gruppiert (`feat` → Added, `fix` → Fixed,
     `perf` → Performance, Rest optional unter „Other“).
5. **Vorschau + Bearbeiten:** Entwurf in einem Editor mit Markdown-Vorschau (vorhandenes `ReadmeView`/`Readme`),
   Nutzer kann anpassen.
6. **Speichern:**
   - `CHANGELOG.md` lesen und dessen Version erkennen (erste Überschrift `## …`, siehe Format).
   - **Gleiche Version** wie die aktuelle → nur `CHANGELOG.md` ersetzen (Neu-Generierung), nichts verschieben.
   - **Andere Version** → alten Inhalt von `CHANGELOG.md` oben in `CHANGELOG_FULL.md` einfügen (nach einem evtl.
     Datei-Kopf), dann `CHANGELOG.md` überschreiben.
   - **`CHANGELOG.md` leer/fehlt** → nur neu anlegen.
   - **`CHANGELOG_FULL.md` fehlt** → mit Kopf anlegen.
   - Doppelte Einträge vermeiden: Ist die alte Version schon in `CHANGELOG_FULL.md`, nicht erneut einfügen.
7. **Ergebnis:** Dateien sind geändert, aber nicht committet; der Git-Tab zeigt sie als Änderungen.
   Optional direkt den Commit-Dialog mit vorbelegter Nachricht öffnen (`docs: changelog for <version>`).

## Vorgeschlagenes Format

Eine Version pro Abschnitt, damit `CHANGELOG.md` direkt als Release-Text nutzbar ist (z. B. vom
`publishMods`-Task: `changelog = file("CHANGELOG.md").readText()`):

```markdown
## 1.4.0 – Minecraft 1.21.1 – 2026-10-17

### Added
- Config screen for the tooltip options

### Fixed
- Crash when opening the inventory with an empty slot

### Performance
- Faster chunk scanning
```

Die erste `## `-Überschrift trägt die Version → daran erkennt das Tool, welche Version in `CHANGELOG.md` steht.
Kopf von `CHANGELOG_FULL.md`: `# Changelog` + eine Leerzeile, danach die Versionen (neueste oben).

## Claude-Integration

- Aufruf: `claude -p "<prompt>"` im Projektordner, Ausgabe = fertiger Markdown-Abschnitt.
- **Keine Werkzeuge für Claude:** Das Tool übergibt alle Commits im Prompt und schreibt die Dateien selbst. Claude
  braucht weder Datei- noch Shell-Zugriff → Tools komplett sperren (genaue CLI-Option beim Start gegen
  `claude --help` prüfen). Dadurch kann der Lauf technisch nichts committen oder verändern.
- Prompt-Inhalt:
  - Mod-Name, Mod-Version, Minecraft-Version, Loader,
  - Commit-Liste (Typ, Betreff, Text; Backport-Hinweis),
  - Format-Vorlage (oben) und Regeln: nutzerorientiert formulieren, Duplikate/Folge-Fixes zusammenfassen,
    interne Commits (`chore`, `ci`, `docs`, `refactor`, `test`, Versions-Bumps) weglassen oder unter „Other“,
    Breaking Changes (`feat!:` / `BREAKING CHANGE:`) hervorheben, Sprache: Englisch,
  - optional der bisherige `CHANGELOG_FULL.md`-Anfang als Stilvorbild,
  - optional ein Abschnitt „Changelog“ aus der CLAUDE.md des Repos, falls vorhanden.
- Antwort prüfen: muss mit `## <aktuelle Version>` beginnen; sonst Fehler anzeigen / Fallback anbieten.

## UI-Skizze

Ort: neuer Tab **„Changelog“** in der Projektansicht (alternativ Abschnitt im Gradle-Tab neben Publish – siehe
offene Fragen).

```
Version: 1.4.0 (gradle.properties)   Minecraft 1.21.1
Commits since: [v1.3.2 ▾]  → 12 commits (3 hidden)          [↻ Reload]
┌───┬─────────┬──────┬───────────────────────────────────────────┐
│ ☑ │ a1b2c3d │ feat │ Add config screen                         │
│ ☑ │ d4e5f6a │ fix  │ Fix crash with empty slot                 │
│ ☐ │ 0f1e2d3 │ chore│ Bump version                              │
└───┴─────────┴──────┴───────────────────────────────────────────┘
[Generate with Claude]  [Generate without Claude]
┌ Editor ──────────────────────────┐┌ Preview ─────────────────────────┐
│ ## 1.4.0 – Minecraft 1.21.1 …    ││ 1.4.0 – Minecraft 1.21.1 …       │
└──────────────────────────────────┘└──────────────────────────────────┘
CHANGELOG.md currently: 1.3.2 → will be moved to the top of CHANGELOG_FULL.md
[Save]  [Save & open commit dialog]
```

Zusätzlich denkbar: Vor `publishMods` warnen, wenn die Version in `CHANGELOG.md` nicht der `mod_version` entspricht.

## Gemeinsame Bausteine mit dem Backport-Assistenten

- **`core/ClaudeRunner`** (neu, einmal bauen, von beiden Features genutzt): findet die `claude`-CLI
  (`SystemActions.findOnPath` / Einstellung), startet `claude -p` mit Prompt (über stdin oder Datei, wegen
  Kommandozeilenlänge), Werkzeug-Freigaben je Feature (Changelog: keine; Backport: eingeschränkte Liste),
  Live-Ausgabe ins Log, Abbrechen, Zeitlimit, optional Kosten-/Token-Anzeige.
- Conventional-Commit-Parser (`type(scope)!: subject`, Marker wie `[no-backport]`) – gemeinsam mit dem
  Backport-Filter.
- Markdown-Vorschau (`ReadmeView`) und Commit-Dialog sind schon vorhanden.

Sinnvolle Reihenfolge: Changelog zuerst bauen (kleiner, Claude ohne Werkzeuge = einfachster Einstieg in
`ClaudeRunner`), danach den Backport-Assistenten darauf aufsetzen.

## Umfang (Schätzung)

| Baustein | Umfang |
|----------|--------|
| `core/ClaudeRunner` (gemeinsam) | ca. 200–300 Zeilen |
| `core/Changelog`: Version/Bereich bestimmen, Commits sammeln + parsen, Fallback-Generator, Datei-Logik `CHANGELOG.md` → `CHANGELOG_FULL.md` | ca. 300–400 Zeilen |
| Tests (Datei-Logik, Versionserkennung, Bereichsbestimmung gegen echte Git-Repos) | ca. 200–300 Zeilen |
| UI-Tab (Commit-Liste, Editor + Vorschau, Speichern) | ca. 300–400 Zeilen |

Gesamt etwa 1 000–1 400 Zeilen inkl. `ClaudeRunner` und Tests.

## Risiken

- **Bereichsbestimmung:** ohne einheitliches Tag-Schema kann die „letzte Version“ falsch erkannt werden → im Dialog
  immer anzeigen und änderbar machen.
- **Manuell gepflegte Changelogs** mit abweichendem Format → Versionserkennung tolerant halten
  (`## [1.4.0]`, `## v1.4.0`, `## 1.4.0 - …`), bei Unklarheit nachfragen statt verschieben.
- **Gleiche Mod-Version auf mehreren MC-Versionen:** Jeder Worktree hat eigene Dateien; Changelogs können sich
  zwischen MC-Versionen unterscheiden (Backports).
- **Kontingent:** jeder Claude-Lauf verbraucht Kontingent; Fallback ohne Claude ist immer verfügbar.

## Offene Fragen

1. **Ort im UI:** eigener Tab „Changelog“ oder Abschnitt im Gradle-Tab (neben Publish)?
2. **Tag-Schema** für die „letzte Version“: `v1.3.2`, `1.3.2`, `1.21.1-1.3.2`, `mc1.21.1-1.3.2` …? Werden überhaupt
   Tags gesetzt, oder soll die Erkennung über `gradle.properties`/`CHANGELOG.md` laufen?
3. **Sprache und Ton** des Changelogs: Englisch? Eher knapp (Stichpunkte) oder erklärend?
4. **Kategorien:** Keep-a-Changelog (`Added/Changed/Fixed/Removed`) oder nach Commit-Typ
   (`Features/Fixes/Performance`)? Interne Commits ganz weglassen oder unter „Other“?
5. **Überschrift:** Minecraft-Version und Datum in der Überschrift (`## 1.4.0 – Minecraft 1.21.1 – 2026-10-17`)?
6. **Kopf von `CHANGELOG_FULL.md`:** nur `# Changelog` oder ein eigener Text?
7. **Gleiche Version erneut generieren:** einfach überschreiben oder vorher fragen?
8. **Mehrere MC-Versionen:** Changelog pro Worktree einzeln, oder optional „für alle Versionen dieser Mod“ in einem
   Durchlauf?
9. **Verknüpfung mit Publish:** Warnung vor `publishMods`, wenn `CHANGELOG.md` nicht zur `mod_version` passt?
10. **Commit danach:** nur Dateien schreiben, oder Button „Save & open commit dialog“ mit vorbelegter Nachricht?

## Einstieg für die neue Sitzung

1. Dieses Dokument (und ggf. [backport-assistant.md](backport-assistant.md)) lesen.
2. Offene Fragen klären.
3. `core/ClaudeRunner` + `core/Changelog` mit JUnit-Tests (Muster: `WorktreesTest`, `GitChangesTest`), danach der UI-Tab.
