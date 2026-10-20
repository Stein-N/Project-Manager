# Feature-Plan: Backport-Assistent (lokal, ohne Commits)

> Status: **Stufe 1 und 2 umgesetzt** (17.10.2026: `core/Backport`, `core/BackportClaude`, `ui/BackportTab`). Siehe
> [Entscheidungen](#entscheidungen-geklärt-am-17102026).
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
2. **Kandidaten:** Commits, die in der Quelle, aber nicht im Ziel sind (`git log <ziel>..<quelle>`), mit Ist in der Quelle ein Release-Tag vorhanden (`git describe --tags`), werden nur Commits seit diesem Tag angeboten (`<tag>..<quelle>`); Tags erstellt und pusht man im Git-Tab (“Create tag…”).
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

## Entscheidungen (geklärt am 17.10.2026)

1. **„Lokal“** = Claude Code CLI auf dem Rechner (Anthropic-API über das Claude-Konto). Kein Ollama.
2. **Granularität:** Die gewählten Commits laufen als **Gruppe** in einem Durchlauf nacheinander
   (`cherry-pick --no-commit`). Bei einem Konflikt hält der Durchlauf an; der Konflikt wird manuell oder per Claude
   gelöst, dann geht es mit dem nächsten Commit weiter. Ein Ergebnis (`decision` + `notes.md`) pro Durchlauf.
3. **Build-Check:** optional per Checkbox („Run build afterwards“).
4. **Typ-Regeln** liegen in den **Tool-Einstellungen** pro Mod und Ziel-Version (nichts im Repo ändern). Beim ersten
   Aufruf werden sie aus `.github/workflows/backport.yml` (`types`) bzw. der CLAUDE.md vorbelegt, falls vorhanden;
   sonst sind alle Typen erlaubt.
5. **Quell-Version:** frei wählbar, Standard = neueste Version der Mod.
6. **Arbeits-Worktree:** `<master>/.backport/<mod>-<ziel>` (versteckter Ordner, vom Scanner ignoriert).
7. **`src/generated/`:** nur Hinweis anzeigen, `runData` nicht automatisch ausführen.
8. **Fehlende CLAUDE.md:** Git-Modus erlaubt, Claude-Modus gesperrt mit Hinweis.
9. **Kosten:** Kosten/Tokens pro Lauf anzeigen (aus dem `result`-Event von `stream-json`), Budget-Limit über
   `--max-budget-usd` (Einstellung).
10. **Gleichzeitigkeit:** höchstens ein Backport pro Ziel-Version.

## Neue Anforderung: gleiche Mod-Version auf allen Minecraft-Versionen (17.10.2026)

Alle Minecraft-Versionen einer Mod sollen immer **dieselbe Mod-Version** tragen; der Backport hebt die älteren
Versionen auf die Version der Quelle an. Die MC-Angabe in `version` (z. B. `1.21.1-2.1.7`) dient nur dazu,
Datei-Konflikte auf CurseForge/Modrinth zu vermeiden. Der Changelog nutzt deshalb nur `#### v<Mod-Version>`
(siehe [Changelog-Assistent](changelog-assistant.md#format)).

Folgen für den Backport:

- Die Regel „Versionen nie portieren“ bleibt für Minecraft-, Loader- und Abhängigkeitsversionen bestehen.
- Der **Mod-Teil** der Version wird gezielt angeglichen: in `gradle.properties` des Ziels nur der Mod-Teil ersetzen,
  das MC-Präfix/-Suffix des Ziels bleibt (`1.20.1-2.1.6` → `1.20.1-2.1.7`). Erkennung des Mod-Teils wie
  `Changelog.displayVersion`.
- `CHANGELOG.md` kommt **nur von `main`**: Der Changelog wird ausschließlich dort erzeugt (siehe
  [Changelog-Assistent](changelog-assistant.md#entscheidungen-geklärt-am-17102026), Punkt 8) und beim Backport ins
  Ziel übernommen; eine ältere Version im Ziel wandert dabei wie gewohnt nach `CHANGELOG_FULL.md` (`Changelog.save`).
- Beides als uncommittete Änderung zusammen mit dem Backport, im Ergebnis sichtbar.
- **Entschieden:** Übernahme von Changelog und Mod-Version passiert **automatisch** als Teil jedes Backports aus
  `main` (Checkbox, standardmäßig an); kein eigener Changelog-Generator auf den anderen Branches.

## Umsetzungsnotizen Stufe 1

- Tab „Backport“ auf jeder Version außer dem Main-Branch (Quelle wählbar, Standard `main`).
- Kandidaten neueste zuerst, **nichts vorausgewählt** („Select portable“ / „None“): die Commits der Mods haben
  meist keinen Conventional-Commit-Typ, und ältere Ziele liegen weit zurück (Resource-Library: 1.21.1 → 188,
  1.20.1 → 245 Commits). Portiert wird immer chronologisch.
- „Bereits portiert“ über `(cherry picked from commit …)`-Trailer im Ziel und `git cherry` (gleicher Patch).
- Arbeits-Worktree `<master>/.backport/<mod>-<ziel>` mit detached HEAD, wird wiederverwendet und vor jedem Lauf
  zurückgesetzt; Prüfung, dass HEAD nach jedem Schritt noch der Ziel-Commit ist.
- „Nie portieren“-Dateien (gradle.properties, mods.toml, neoforge.mods.toml, `src/generated/`, `.github/`, Changelogs)
  bleiben wie im Ziel – auch ihre Konflikte werden automatisch so gelöst (typisch: Versionszeile).
- Konflikte: Lauf hält an, Konfliktdateien mit Diff bzw. Erklärung bei gelöschten Dateien; lösen in IntelliJ im
  Arbeitsordner, „Continue“ prüft auf Konfliktmarker und macht weiter.
- „Take over version and CHANGELOG.md“ (Standard an): Mod-Teil der Version + `CHANGELOG.md` der Quelle.
- Apply per `git diff --binary --output` + `git apply --3way` als uncommittete Änderung, danach Commit-Dialog mit
  vorbelegter Nachricht inkl. Trailer.
- Praxisbefund: 26.1/26.2 enthalten `main` bereits (Merge); bei 1.21.1 fehlen viele Dateien (z. B. Forge-Teil,
  Fabric-Hauptklasse) → viele modify/delete-Konflikte. Für solche Backports ist Stufe 2 (Claude) gedacht.

## Plan Stufe 2 (Claude) – Entscheidungen vom 17.10.2026

- **Regeln eingebaut:** Die Mods haben keine `CLAUDE.md`. Das Tool bringt die Backport-Regeln aus der Vorlage selbst mit
  (Verhalten des Commits erhalten, an die Ziel-API anpassen, „Nie portieren“-Liste, nicht committen, im Zweifel
  `skip`); eine `CLAUDE.md` im Repo ergänzt sie, wenn vorhanden. Ersetzt Entscheidung 8 (kein Sperren mehr).
- **Umfang: Konflikte + Build.**
  - „Resolve with Claude“ bei einem Konflikt; optional Checkbox „Resolve conflicts with Claude“ für den ganzen Lauf
    (hält nur noch an, wenn Claude `skip` meldet oder scheitert).
  - „Fix build with Claude“ nach einem fehlgeschlagenen `gradlew build` im Arbeitsordner.
  - Claude antwortet je Aufgabe mit `backport`/`skip` und Notizen („Was wurde angepasst“ / „Bitte prüfen“).
  - Zwischenstand vor jedem Commit (`git write-tree`), damit `skip` genau diesen Commit zurücknehmen kann (auch als
    Button „Skip this commit“ ohne Claude).
- **Modell: Opus 5.5** als Standard für Backports (eigene Einstellung „Backport model“, Standard `claude-opus-5-5`);
  der Changelog bleibt bei Sonnet. Mit dem Pro-Token getestet: Opus 5.5 ist verfügbar.
- **Werkzeuge:** Lesen/Bearbeiten im Arbeitsordner, `git status/diff/log/show`, `gradlew build`; alles andere wird
  automatisch abgelehnt (`--permission-mode dontAsk`), insbesondere `git commit/push/checkout/switch/branch/reset/
  rebase/worktree`. Nach jedem Lauf: HEAD des Arbeitsordners und alle Branches unverändert, sonst verwerfen.
  Genaue CLI-Optionen vor der Umsetzung gegen die echte CLI prüfen.
- **Tests:** Fake-CLI (Ablauf, Abbruch, Verstoß gegen die Sperren) und ein echter Lauf an einer Kopie von
  Resource-Library (Ziel 1.21.1).

## Umsetzungsnotizen Stufe 2

- Aufruf: `claude -p --restricted --permission-mode dontAsk --permission-prompts none` mit `--tools Read,Edit,Write,
  Glob,Grep,Bash`, Allow-Liste (Dateien, `git status/diff/log/show/ls-files/grep/ls-tree/cat-file/blame/rm`,
  `./gradlew build/compileJava`) und expliziter Deny-Liste (u. a. `git commit/push/checkout/switch/branch/reset/
  rebase/worktree/cherry-pick/merge/stash/add`, `rm`). Gegen die echte CLI geprüft: commit, checkout -b, reset --hard,
  Schreiben außerhalb des Ordners (Bash und Write), `.git/hooks` und `rm` werden abgelehnt; abgelehnte Aufrufe
  (`permission_denials`) stehen in den Notizen.
- Vor/nach jedem Claude-Lauf: Branches, Tags und HEAD des Arbeitsordners verglichen; bei Abweichung Abbruch.
- Zwischenstand vor jedem Commit (`git write-tree`, Wiederherstellung mit `read-tree --reset -u` + `clean -fd`):
  „Skip this commit“ und Claudes `skip` nehmen genau diesen Commit zurück.
- Version und `CHANGELOG.md` werden nur übernommen, wenn mindestens ein Commit portiert wurde.
- Reiter „Notes“ (Markdown): pro Commit Claudes Entscheidung, Kosten, Notizen, abgelehnte Aufrufe; dazu die
  Hinweise des Tools (zurückgesetzte Dateien, `runData`, Build-Ergebnis).
- Einstellungen: „Backport model“ (Standard `claude-opus-5-5`) und eigenes Budget (Standard 5 $ pro Lauf);
  Zeitlimit mindestens 30 min.
- Echte Läufe: Resource-Library `86835d5` → 1.21.1: Claude entscheidet begründet `skip` (Funktion und Paket
  `net.morthen.*` gibt es dort nicht), Commit vollständig zurückgenommen, 0,12 $. Gebauter Konflikt
  (Umbenennung vs. geänderter Wert): Claude löst ihn, behält den Wert des Ziels, 0,05 $
  (`BackportClaudeSmokeTest`, nur mit `CLAUDE_SMOKE=1`).

## Einstieg für die neue Sitzung

1. Dieses Dokument und `C:\Users\Nico\Desktop\CLAUDE.md` lesen.
2. Entscheidungen oben beachten (Fragen sind geklärt).
3. Mit Stufe 1 beginnen: zuerst `core/Backport` (Kandidaten, Filter, Marker, „bereits portiert“, Arbeits-Worktree,
   Cherry-Pick, Apply/Discard) mit JUnit-Tests gegen echte Git-Repos (Muster: `WorktreesTest`), danach der UI-Tab.
