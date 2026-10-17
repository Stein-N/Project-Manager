# Feature-Plan: Backport-Assistent (lokal, ohne Commits)

> Status: **geplant, noch nicht begonnen.** Dieses Dokument ist der Startpunkt für eine neue Sitzung.
> Vor dem Start die [offenen Fragen](#offene-fragen) klären.
> Verwandtes Feature: [Changelog-Assistent](changelog-assistant.md) – beide nutzen einen gemeinsamen
> `core/ClaudeRunner`; sinnvoll ist, den Changelog zuerst zu bauen (Claude ohne Werkzeuge) und den Backport darauf
> aufzusetzen.

## Ziel

Ein neuer Tab **„Backport“** in der Projektansicht übernimmt Commits aus einer Minecraft-Version einer Mod
(z. B. `main` / 1.21.4) in eine ältere Version derselben Mod (z. B. 1.21.1, 1.20.1) – nach den Regeln aus der
`CLAUDE.md` des Mod-Repos. Konflikte und API-Unterschiede kann optional **Claude Code** auflösen.

Harte Anforderungen:

- **Alles läuft lokal** auf dem Rechner (kein GitHub-Actions-Lauf, kein Push, kein PR).
- **Das Feature löst keine Commits aus.** Ergebnis sind ausschließlich *uncommittete* Änderungen im
  Ziel-Worktree, die der Nutzer prüft und selbst committet.

## Ausgangslage

Die Vorlage `C:\Users\Nico\Desktop\CLAUDE.md` beschreibt einen Backport-Workflow für GitHub Actions
(Claude in CI, Ergebnis = Commits + PR). Die für dieses Feature relevanten Regeln:

| Regel | Inhalt |
|-------|--------|
| Commit-Typen pro Ziel | `1.21.1`: `fix`, `feat`, `perf` · `1.20.1`: nur `fix` (im Original in `.github/workflows/backport.yml` unter `types`) |
| Commits ohne Conventional-Commit-Präfix | werden mitgenommen, Mensch entscheidet |
| Marker | `[no-backport]` → nirgends portieren · `[no-backport: 1.20.1]` → nur dort nicht |
| Nie portieren | Versionen/Abhängigkeitsbereiche in `gradle.properties`, `mods.toml`, `neoforge.mods.toml`; `src/generated/` (im Ziel mit `./gradlew runData` neu erzeugen); alles unter `.github/`; Code ohne sinnvolle Entsprechung in der Zielversion → `skip` mit Begründung |
| API-Unterschiede | Tabelle in der CLAUDE.md (NeoForge ↔ Forge: Packages, Event-Bus, `ResourceLocation`, Data Components ↔ NBT, Netzwerk, Metadaten) |
| Erkennung bereits portierter Commits | Zeile `(cherry picked from commit …)` aus `git cherry-pick -x` |
| Ergebnis | `.backport/decision` (`backport` oder `skip`), `.backport/notes.md` (Format: „Was wurde angepasst“ / „Bitte prüfen“); `.backport/` ist git-ignoriert |
| Grenzen | nie pushen, keine Branches wechseln/anlegen/löschen, keine Versionen ändern, keine Abhängigkeiten hinzufügen, keine Tests löschen; im Zweifel `skip` |

**Widerspruch zur Anforderung:** Die CLAUDE.md verlangt `cherry-pick -x` (= Commits), eigene Commits für
Anpassungen und „nichts uncommitted lassen“. Für den lokalen Modus müssen diese Schritte ersetzt werden
(siehe [Sicherheitskonzept](#sicherheitskonzept-keine-commits)). Die CLAUDE.md selbst bleibt unverändert, damit ein
CI-Workflow weiter funktioniert.

## Bestehende Bausteine im Tool

Das Tool (Java 25, Swing + FlatLaf IntelliJ, Gradle) bringt fast alles Nötige mit:

| Klasse | Relevanz |
|--------|----------|
| `core/Worktrees` | gemeinsames Bare-Repo pro Mod unter `<master>/.repos/<mod>.git`, jede MC-Version ist ein Worktree → alle Versionen sehen dieselben Commits; `add`, `remove`, `list`, `commonDir` |
| `core/Git` | `run`/`runOrThrow` (Ausgabe zeilenweise streambar), `status`, `branches` |
| `core/GitChanges` | geänderte Dateien, Diff einer Datei, Commit/Push (Commit-Dialog) |
| `core/GradleRunner` | Gradle-Tasks mit Live-Ausgabe, Stop inkl. Kindprozessen, Umgebungsvariablen – Muster für einen `claude`-Prozess |
| `core/EnvFile` | `.env` im Master-Ordner |
| `ui/MainFrame` | Projekt-Tabs (`projectDetails`: Overview, README, Git, Gradle, Dependencies), Log-Bereich, `background(...)`-Helfer |
| `ui/CommitDialog` | Dateiliste + farbiger Diff (`renderDiff`) – wiederverwendbar für die Ergebnis-Ansicht |
| `ui/ReadmeView` + `core/Readme` | Markdown-Rendering (commonmark + JavaFX WebView) – für `.backport/notes.md` |
| `core/SystemActions` | Prozess-/Plattform-Helfer (Windows/Linux) |

Projekt-Konventionen: UI-Texte auf Englisch; jede Änderung wird committet und nach `origin/main` gepusht;
Fenster fest 900×680.

Lokale Voraussetzungen (Stand 17.10.2026): Claude Code CLI ist installiert
(`C:\Users\Nico\AppData\Local\Microsoft\WinGet\Links\claude.exe`, Version 2.1.296). Ollama ist **nicht** installiert.

## Geplanter Aufbau

### Ablauf

1. **Auswahl:** Ziel = das im Baum gewählte Projekt (z. B. `1.20.1/Mod`), Quelle = andere Version derselben Mod
   (Auswahl unter den Geschwister-Worktrees, Standard: neueste Version / `main`).
2. **Kandidaten:** Commits, die in der Quelle, aber nicht im Ziel sind (`git log <ziel>..<quelle>`), mit
   - Conventional-Commit-Typ und Filter pro Ziel,
   - Marker `[no-backport]` / `[no-backport: <ziel>]`,
   - Status „bereits portiert“ (Trailer `(cherry picked from commit <sha>)` im Ziel und `git cherry` / Patch-ID).
3. **Arbeits-Worktree:** temporärer Worktree mit *detached HEAD* auf dem Stand des Ziel-Branches,
   z. B. `<master>/.backport/<mod>-<ziel>` (versteckter Ordner → vom Scanner ignoriert). Pro Ziel wiederverwenden,
   damit Gradle nicht jedes Mal neu konfiguriert.
4. **Übernehmen:** pro gewähltem Commit `git cherry-pick --no-commit <sha>`.
   - Ohne Konflikt → weiter.
   - Mit Konflikt → Stufe 1: anzeigen und Nutzer entscheiden lassen; Stufe 2: Claude auflösen lassen.
   - Dateien aus „Nie portieren“ werden zurückgesetzt (`git checkout <ziel> -- <pfad>` im Arbeits-Worktree).
5. **Prüfen (optional):** `./gradlew build` im Arbeits-Worktree über den `GradleRunner`.
6. **Ergebnis anzeigen:** Entscheidung, Notizen (Markdown), Dateiliste + Diff.
7. **Übernehmen oder verwerfen:**
   - **Apply** → Diff des Arbeits-Worktrees als uncommittete Änderungen in den echten Ziel-Worktree
     (`git diff --binary` + `git apply --3way`), danach Arbeits-Worktree zurücksetzen.
   - **Discard** → Arbeits-Worktree zurücksetzen (`git reset --hard <ziel>` + `git clean -fd` **nur** dort).
8. Beim späteren Commit über den Commit-Dialog die Nachricht mit `(cherry picked from commit <sha>)`
   vorbelegen, damit spätere Läufe (und ein CI-Workflow) den Commit als portiert erkennen.

### Claude-Integration (Stufe 2)

- Aufruf headless im Arbeits-Worktree: `claude -p "<prompt>" --output-format stream-json` (Live-Log,
  Abbrechen-Button, Zeitlimit). Genaue CLI-Optionen beim Start gegen `claude --help` prüfen.
- Prompt = Commit-Infos (SHA, Nachricht, Diff, Konfliktdateien) + Verweis auf die CLAUDE.md des Repos +
  **lokaler Zusatz**, der die CI-Schritte ersetzt:
  „Nicht committen. Änderungen nur im Arbeitsverzeichnis/Index lassen. Kein Branch-Wechsel, kein Push.
  Ergebnis in `.backport/decision` und `.backport/notes.md` schreiben.“
- Erlaubte Werkzeuge per Allow-/Deny-Liste einschränken: Lesen/Bearbeiten von Dateien, `git status/diff/log/show`,
  `git cherry-pick --no-commit`, `./gradlew build`; verboten u. a. `git commit`, `git push`, `git checkout`,
  `git switch`, `git branch`, `git reset --hard`, `git rebase`, `git worktree`.
- Danach `.backport/decision` und `.backport/notes.md` auslesen.
- Hinweis anzeigen, wenn die Mod keine CLAUDE.md hat (optional: Vorlage als lokale, nicht committete Datei anlegen).

### UI-Skizze (Tab „Backport“)

```
Source: [1.21.4 ▾]   Target: 1.20.1        Rules: fix only            [↻ Refresh]
┌───┬─────────┬──────┬──────────────────────────────────┬──────────────────────┐
│ ☑ │ a1b2c3d │ fix  │ Fix crash when …                 │                      │
│ ☐ │ d4e5f6a │ feat │ Add config screen                │ filtered (type)      │
│ ☐ │ 0f1e2d3 │ fix  │ Fix tooltip [no-backport: 1.20.1]│ marker               │
│ ☐ │ 9a8b7c6 │ fix  │ Fix sync                         │ already ported       │
└───┴─────────┴──────┴──────────────────────────────────┴──────────────────────┘
[Port selected]  [Port with Claude]  ☐ Run build afterwards
--- Ergebnis --------------------------------------------------------------
Decision: backport     Notes (Markdown)     Files + Diff
[Apply to 1.20.1 (uncommitted)]  [Discard]  [Open work folder in IntelliJ]
```

## Sicherheitskonzept (keine Commits)

Mehrere Ebenen, damit das Feature nie einen Commit auf einem echten Branch erzeugt:

1. **Isolierter Arbeits-Worktree mit detached HEAD.** Selbst ein versehentlicher Commit dort hängt an keinem
   Branch; die Branches der Versionen werden nie bewegt.
2. **Werkzeug-Sperren für `claude`** (siehe oben).
3. **Lokaler Prompt-Zusatz**, der die Commit-Schritte der CLAUDE.md ersetzt.
4. **Kontrolle nach jedem Lauf:** `HEAD` des Arbeits-Worktrees und alle Branch-Refs vorher/nachher vergleichen;
   bei Abweichung Lauf verwerfen und melden.
5. **Übernahme nur nach Prüfung** als uncommittete Änderung; Commit macht der Nutzer selbst.

## Umfang (Schätzung)

| Stufe | Inhalt | Umfang |
|-------|--------|--------|
| 1 – Backport-Tab (nur Git) | Kandidaten + Filter + Marker + „bereits portiert“, Arbeits-Worktree, `cherry-pick --no-commit`, Konfliktanzeige, Ausschluss-Pfade, optionaler Build, Diff-Ansicht, Apply/Discard, Commit-Nachricht vorbelegen, Tests | ca. 1 000–1 300 Zeilen |
| 2 – Claude | `claude -p`-Integration mit Werkzeug-Sperren, Live-Log, Abbruch/Zeitlimit, Prompt-Aufbau, Auswertung `decision`/`notes.md`, Markdown-Anzeige, CLAUDE.md-Hinweis | ca. 600–900 Zeilen |

Stufe 1 ist für sich schon nutzbar (konfliktfreie Backports ohne KI).

## Risiken

- **Gradle-Zeit:** frischer Arbeits-Worktree → Gradle-Konfiguration (Forge/NeoForge beim ersten Mal mehrere Minuten);
  Worktree pro Ziel wiederverwenden.
- **Kosten/Kontingent:** jeder Claude-Lauf verbraucht Kontingent des Claude-Kontos.
- **Doppelte Backports** mit einem parallelen CI-Workflow → abgefangen über `-x`-Trailer und `git cherry`, solange der
  Trailer beim Commit erhalten bleibt.
- **CLAUDE.md-Qualität:** Die Vorlage enthält Platzhalter (`<ModName>`, Beispiel-Tabellen); ohne ausgefüllte Regeln
  rät Claude.
- **`git apply` beim Übernehmen** kann scheitern, wenn der Ziel-Worktree inzwischen eigene Änderungen hat →
  vorher prüfen/warnen, `--3way` nutzen.

## Offene Fragen

1. **Was heißt „lokal“?**
   - (a) Claude Code CLI auf dem Rechner – Modell läuft über die Anthropic-API (Internet + Claude-Konto nötig).
     *Empfehlung.*
   - (b) Komplett offline mit lokalem Modell (z. B. Ollama + Code-Modell) – schwächere Qualität bei
     NeoForge↔Forge-Umbauten, starke GPU nötig, Ollama nicht installiert.
   - (c) Beides, wählbar.
2. **Granularität:** jeden Commit einzeln portieren (eigene Entscheidung/Notizen pro Commit) oder mehrere Commits in
   einem Durchlauf als Gruppe?
3. **Build-Check:** Pflicht vor „Apply“ oder optional (Checkbox)?
4. **Woher kommen die Typ-Regeln pro Ziel?**
   - aus `.github/workflows/backport.yml` lesen,
   - aus der CLAUDE.md lesen,
   - oder in den Tool-Einstellungen pro Mod/Ziel pflegen (nichts im Repo ändern)?
5. **Quell-Version:** immer die neueste Version / `main`, oder frei wählbar?
6. **Ort des Arbeits-Worktrees:** `<master>/.backport/…` (sichtbar im Master-Ordner, vom Scanner ignoriert) oder im
   System-Temp-Ordner?
7. **`src/generated/`:** nach dem Backport automatisch `./gradlew runData` im Arbeits-Worktree ausführen oder nur
   darauf hinweisen?
8. **Fehlende CLAUDE.md:** Backport trotzdem erlauben (nur Git-Modus), Vorlage anbieten oder Claude-Modus sperren?
9. **Kosten-Anzeige:** Soll das Tool Token/Kosten pro Lauf anzeigen (sofern die CLI sie liefert) oder ein Limit setzen?
10. **Gleichzeitigkeit:** Soll pro Mod nur ein Backport gleichzeitig laufen, oder pro Ziel-Version einer?

## Einstieg für die neue Sitzung

1. Dieses Dokument und `C:\Users\Nico\Desktop\CLAUDE.md` lesen.
2. Offene Fragen mit dem Nutzer klären.
3. Mit Stufe 1 beginnen: zuerst `core/Backport` (Kandidaten, Filter, Marker, „bereits portiert“, Arbeits-Worktree,
   Cherry-Pick, Apply/Discard) mit JUnit-Tests gegen echte Git-Repos (Muster: `WorktreesTest`), danach der UI-Tab.
