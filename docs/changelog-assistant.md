# Feature-Plan: Changelog-Assistent (lokal, mit Claude)

> Status: **umgesetzt** (17.10.2026) – `core/ClaudeRunner`, `core/ConventionalCommit`, `core/Changelog`, `ui/ChangelogTab`.
> Entscheidungen siehe [unten](#entscheidungen-geklärt-am-17102026), Abweichungen unter [Umsetzungsnotizen](#umsetzungsnotizen).
> Verwandtes Feature: [Backport-Assistent](backport-assistant.md)
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
2. **Commit-Bereich bestimmen** (Start = „letzte Version“, Ende = `HEAD` des Worktrees). Keine Git-Tags; Kandidaten
   für den Start, alle im Dialog wählbar, Vorauswahl in dieser Reihenfolge:
   1. der Commit, der `CHANGELOG.md` zuletzt geändert hat – wenn dort eine **andere** Version als die aktuelle steht
      (= Release der vorherigen Version),
   2. der Commit, der `mod_version` auf den **aktuellen** Wert gesetzt hat (Versions-Bump nach dem Release);
      liegen danach keine Commits mehr (Bump erst beim Release), stattdessen der Commit, der den **vorherigen** Wert
      gesetzt hat,
   3. manuell im Dialog wählbar (Start-Commit).
3. **Commits sammeln:** `git log <start>..HEAD` mit SHA, Betreff, Text, Autor, Datum; gruppiert nach
   Conventional-Commit-Typ. Backport-Commits mit `(cherry picked from commit …)` bleiben erhalten (Hinweis „backport“).
   Merge-Commits und reine Versions-/CI-Commits werden standardmäßig ausgeblendet.
4. **Entwurf erzeugen:**
   - **Mit Claude:** Commit-Liste + Vorlage + Regeln (siehe unten) → Claude liefert den Markdown-Text auf stdout.
   - **Ohne Claude (Fallback):** ein Stichpunkt pro Commit-Betreff, älteste zuerst.
5. **Vorschau + Bearbeiten:** Entwurf in einem Editor mit Markdown-Vorschau (vorhandenes `ReadmeView`/`Readme`),
   Nutzer kann anpassen.
6. **Speichern:**
   - `CHANGELOG.md` lesen und dessen Version erkennen (erste Überschrift mit Version, siehe [Format](#format)).
   - **Gleiche Version** wie die aktuelle → nur `CHANGELOG.md` ersetzen (Neu-Generierung), nichts verschieben.
   - **Andere Version** → alten Inhalt von `CHANGELOG.md` oben in `CHANGELOG_FULL.md` einfügen (nach einem evtl.
     Datei-Kopf), dann `CHANGELOG.md` überschreiben.
   - **`CHANGELOG.md` leer/fehlt** → nur neu anlegen.
   - **`CHANGELOG_FULL.md` fehlt** → mit Kopf anlegen.
   - Doppelte Einträge vermeiden: Ist die alte Version schon in `CHANGELOG_FULL.md`, nicht erneut einfügen.
7. **Ergebnis:** Dateien sind geändert, aber nicht committet; der Git-Tab zeigt sie als Änderungen.
   Optional direkt den Commit-Dialog mit vorbelegter Nachricht öffnen (`docs: changelog for <version>`).

## Format

Das **bisherige Format** der Mods wird beibehalten (geändert am 17.10.2026, ersetzt den ursprünglichen Vorschlag
mit Keep-a-Changelog-Abschnitten und MC-Version/Datum in der Überschrift):

```markdown
#### v2.1.7
- update LootTableModifier, uses now a Supplier instead of a fixed value
- adding Model Render Helper
```

- Überschrift nur mit der **Mod-Version** (`#### v<version>`), ohne Minecraft-Version und Datum: Alle
  Minecraft-Versionen einer Mod tragen dieselbe Mod-Version (über den Backport werden alle auf diese Version
  angehoben). Der MC-Teil in `version=1.21.1-2.1.7` dient nur dazu, Datei-Konflikte auf CurseForge/Modrinth zu
  vermeiden, und erscheint nicht im Changelog.
- Darunter eine flache Stichpunktliste, keine Unterabschnitte.
- `CHANGELOG.md` ist direkt als Release-Text nutzbar (z. B. `changelog = file("CHANGELOG.md").readText()`).
- Erkennung: die erste Überschrift beliebiger Ebene, die mit einer Version beginnt.
- Kopf von `CHANGELOG_FULL.md` nur beim Neuanlegen: `# Changelog` (bestehende Dateien bleiben, wie sie sind).

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
- Antwort prüfen: muss mit `#### v<aktuelle Version>` beginnen; sonst Entwurf mit Warnung in den Editor.

## UI-Skizze

Ort: neuer Tab **„Changelog“** in der Projektansicht.

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
│ #### v1.4.0                      ││ v1.4.0                           │
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

- **Bereichsbestimmung:** ohne Tags kann die „letzte Version“ falsch erkannt werden → im Dialog
  immer anzeigen und änderbar machen.
- **Manuell gepflegte Changelogs** mit abweichendem Format → Versionserkennung tolerant halten
  (`## [1.4.0]`, `## v1.4.0`, `## 1.4.0 - …`), bei Unklarheit nachfragen statt verschieben.
- **Gleiche Mod-Version auf mehreren MC-Versionen:** Jeder Worktree hat eigene Dateien; Changelogs können sich
  zwischen MC-Versionen unterscheiden (Backports).
- **Kontingent:** jeder Claude-Lauf verbraucht Kontingent; Fallback ohne Claude ist immer verfügbar.

## Entscheidungen (geklärt am 17.10.2026)

1. **Ort im UI:** eigener Tab **„Changelog“** in der Projektansicht.
2. **Keine Git-Tags.** Die „letzte Version“ wird über die letzte Änderung an `CHANGELOG.md` bzw. an `mod_version`
   in `gradle.properties` erkannt (Details unter [Ablauf](#ablauf), Schritt 2); im Dialog immer änderbar.
3. **Sprache und Ton:** Englisch, knappe Stichpunkte.
4. **Keine Kategorien** – flache Stichpunktliste im bisherigen Stil (geändert, siehe [Format](#format)). Interne
   Commits (`chore`, `ci`, `docs`, `refactor`, `test`, `build`, `style`, Versions-Bumps) werden weggelassen.
5. **Überschrift** `#### v<Mod-Version>` ohne Minecraft-Version und Datum (geändert, siehe [Format](#format)).
6. **Kopf von `CHANGELOG_FULL.md`:** nur `# Changelog`.
7. **Gleiche Version erneut generieren:** vorher nachfragen, dann überschreiben.
8. **Nur auf dem Main-Branch** (geändert am 17.10.2026): Der Changelog wird nur auf dem Main-Branch erzeugt
   (`origin/HEAD`, sonst lokales `main`/`master`; bei den Mods `main` im neuesten MC-Ordner). Die anderen Versionen
   bekommen ihn über den Backport, da die Änderungen dieselben sind. Auf anderen Branches zeigt der Tab nur einen
   Hinweis, die Version der dortigen `CHANGELOG.md` und einen Sprung zur Main-Version.
9. **Publish:** Warnung vor `publishMods`, wenn die Version in `CHANGELOG.md` nicht zur `mod_version` passt.
10. **Commit danach:** „Save“ und „Save & open commit dialog“ (Nachricht `docs: changelog for <version>`).

## Umsetzungsnotizen

- **Versionserkennung** akzeptiert Überschriften jeder Ebene (`## 1.4.0 – …`, `## [1.4.0]`, `#### v2.1.6`), weil
  bestehende Changelogs (z. B. Resource-Library) `#### vX.Y.Z` nutzen. Neue Einträge werden oberhalb der ersten
  Versions-Überschrift in `CHANGELOG_FULL.md` eingefügt, egal welcher Ebene.
- **Anzeige-Version:** `mod_version`/`version` kann die MC-Version enthalten (`1.21.1-2.1.6`, `2.1.6+1.21.1`); im
  Changelog wird sie ohne MC-Teil verwendet (`2.1.6`), Vergleiche ignorieren ein führendes `v`.
- **Claude ohne Werkzeuge:** `--tools ""` allein reicht nicht – die MCP-Server des Kontos bleiben sonst aktiv. Daher
  immer `--strict-mcp-config` mit leerer MCP-Konfiguration. `--bare` scheidet aus (kein OAuth-Login).
- **UI:** Das Fenster ist fest 900×680, der Tab hat nur ca. 610×300 px → zwei Unter-Reiter „Commits“ und „Draft“;
  die Vorschau nutzt ein Swing-`JEditorPane` (der JavaFX-WebView ist an den README-Tab gebunden). Eine Tab-Instanz pro
  Projekt überlebt die häufigen Neuaufbauten der Detailansicht (Entwurf und laufender Claude-Lauf bleiben erhalten).
- Einstellungen: Pfad zur Claude-CLI, Modell, Budget pro Lauf (Standard 1 $), Zeitlimit (Standard 15 min).

## Einstieg für die neue Sitzung

1. Dieses Dokument (und ggf. [backport-assistant.md](backport-assistant.md)) lesen.
2. Entscheidungen oben beachten (Fragen sind geklärt).
3. `core/ClaudeRunner` + `core/Changelog` mit JUnit-Tests (Muster: `WorktreesTest`, `GitChangesTest`), danach der UI-Tab.
