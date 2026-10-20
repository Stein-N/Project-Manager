//! The mod files of all loaders in a project (fabric.mod.json, quilt.mod.json, neoforge.mods.toml, mods.toml – in
//! the root and in subprojects like `fabric/` or `neoforge/`) and the version declarations of their dependencies,
//! which can be changed in place without touching the rest of the file.

use crate::metadata::{expand, strip_comment, unquote};
use crate::{dependencies, util};
use anyhow::{Context, Result, bail};
use pm_types::{DeclaredDependency, DependencyKind, ModFile, ModLoader, VersionConstraint};
use regex::Regex;
use serde_json::Value;
use std::collections::BTreeMap;
use std::fs;
use std::ops::Range;
use std::path::{Component, Path};
use std::sync::LazyLock;

const FILES: [(&str, ModLoader); 4] = [
    ("fabric.mod.json", ModLoader::Fabric),
    ("quilt.mod.json", ModLoader::Quilt),
    ("META-INF/neoforge.mods.toml", ModLoader::NeoForge),
    ("META-INF/mods.toml", ModLoader::Forge),
];
/// Sections of fabric.mod.json with dependencies and whether they are required.
const FABRIC_SECTIONS: [(&str, bool); 3] = [("depends", true), ("recommends", false), ("suggests", false)];
static PLACEHOLDER: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\$\{[A-Za-z0-9_.]+}").unwrap());

pub fn read(project: &Path) -> Vec<ModFile> {
    let gradle = util::read_properties(&project.join("gradle.properties"));
    let suggest = Suggestions::new(project, &gradle);
    let mut files = Vec::new();
    for base in bases(project) {
        let resources = base.join("src/main/resources");
        for (name, loader) in FILES {
            let file = resources.join(name);
            let Ok(text) = fs::read_to_string(&file) else { continue };
            let found = match loader {
                ModLoader::Fabric => fabric(&text).map(|d| (d, true)),
                ModLoader::Quilt => quilt(&expand(&text, &gradle)).map(|d| (d, false)),
                _ => Some((
                    toml_blocks(&text, &gradle).into_iter().map(|b| (b.mod_id, b.version, b.required)).collect(),
                    true,
                )),
            };
            let Some((deps, editable)) = found else { continue };
            let dependencies = deps
                .into_iter()
                .map(|(raw_id, raw, required)| {
                    let mod_id = expand(&raw_id, &gradle);
                    DeclaredDependency {
                        constraint: VersionConstraint::parse(loader, &raw),
                        resolved: expand(&raw, &gradle),
                        suggestions: suggest.of(&mod_id, &raw),
                        raw_id,
                        mod_id,
                        raw,
                        required,
                    }
                })
                .collect();
            let relative = file.strip_prefix(project).unwrap_or(&file).to_string_lossy().replace('\\', "/");
            files.push(ModFile { loader, file: relative, dependencies, editable });
        }
    }
    files
}

/// The project and its direct subprojects with resources (root first, then alphabetically).
fn bases(project: &Path) -> Vec<std::path::PathBuf> {
    let mut subs: Vec<_> = fs::read_dir(project)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| e.path())
        .filter(|p| {
            let name = util::file_name(p);
            !name.starts_with('.') && name != "build" && p.join("src/main/resources").is_dir()
        })
        .collect();
    subs.sort();
    std::iter::once(project.to_path_buf()).filter(|p| p.join("src/main/resources").is_dir()).chain(subs).collect()
}

/// Sets how the version of `raw_id` in `file` is evaluated; returns the previous declaration.
pub fn set(project: &Path, file: &str, raw_id: &str, constraint: &VersionConstraint) -> Result<String> {
    let rel = Path::new(file);
    if rel.is_absolute() || rel.components().any(|c| matches!(c, Component::ParentDir)) {
        bail!("{file} is not inside the project");
    }
    let Some((_, loader)) = FILES.iter().find(|(name, _)| file.ends_with(name)) else { bail!("{file} is no mod file") };
    let path = project.join(rel);
    let text = fs::read_to_string(&path).with_context(|| format!("Cannot read {file}"))?;
    let value = constraint.format(*loader);
    if value.contains(['"', '\n']) {
        bail!("Invalid version: {value}");
    }
    let (updated, old) = match loader {
        ModLoader::Fabric => {
            let (span, old) = FABRIC_SECTIONS
                .iter()
                .find_map(|(section, _)| {
                    let span = Json::new(&text).find(&[section, raw_id])?;
                    Some((span.clone(), fabric_version(&serde_json::from_str(&text[span]).ok()?)))
                })
                .with_context(|| format!("{raw_id} not found in {file}"))?;
            (splice(&text, span, &serde_json::to_string(&value)?), old)
        }
        ModLoader::Quilt => bail!("quilt.mod.json cannot be changed here"),
        _ => {
            let gradle = util::read_properties(&project.join("gradle.properties"));
            let block = toml_blocks(&text, &gradle)
                .into_iter()
                .find(|b| b.mod_id == raw_id)
                .with_context(|| format!("{raw_id} not found in {file}"))?;
            let updated = match block.version_span {
                Some(span) => splice(&text, span, &value),
                None => {
                    // a new line below modId, indented like it
                    let at = block.insert_at;
                    let modid_line = text[..at].trim_end_matches(['\r', '\n']).rsplit('\n').next().unwrap_or("");
                    let indent: String = modid_line.chars().take_while(|c| c.is_whitespace()).collect();
                    let newline = if text.contains("\r\n") { "\r\n" } else { "\n" };
                    let line = format!("{indent}versionRange = \"{value}\"{newline}");
                    let line = if text[..at].ends_with('\n') { line } else { format!("{newline}{line}") };
                    splice(&text, at..at, &line)
                }
            };
            (updated, block.version)
        }
    };
    fs::write(&path, updated)?;
    Ok(old)
}

fn splice(text: &str, span: Range<usize>, with: &str) -> String {
    format!("{}{with}{}", &text[..span.start], &text[span.end..])
}

// ---------------------------------------------------------------- Fabric / Quilt

/// (raw id, raw version, required) of fabric.mod.json, read before placeholders are resolved.
fn fabric(text: &str) -> Option<Vec<(String, String, bool)>> {
    let json: Value = serde_json::from_str(text).ok()?;
    let mut deps = Vec::new();
    for (section, required) in FABRIC_SECTIONS {
        for (id, v) in json[section].as_object().into_iter().flatten() {
            deps.push((id.clone(), fabric_version(v), required));
        }
    }
    Some(deps)
}

/// A version string, or alternatives (`["1.0", "2.0"]`) joined with " || ".
fn fabric_version(v: &Value) -> String {
    match v {
        Value::Array(a) => a.iter().filter_map(Value::as_str).collect::<Vec<_>>().join(" || "),
        other => other.as_str().unwrap_or("*").to_string(),
    }
}

fn quilt(text: &str) -> Option<Vec<(String, String, bool)>> {
    let root: Value = serde_json::from_str(text).ok()?;
    let deps = root.get("quilt_loader")?["depends"].as_array()?;
    Some(
        deps.iter()
            .map(|d| match d {
                Value::Object(o) => (
                    o.get("id").and_then(Value::as_str).unwrap_or_default().to_string(),
                    o.get("versions")
                        .map(|v| v.as_str().map(str::to_string).unwrap_or(v.to_string()))
                        .unwrap_or("*".into()),
                    !o.get("optional").and_then(Value::as_bool).unwrap_or(false),
                ),
                other => (other.as_str().unwrap_or_default().to_string(), "*".into(), true),
            })
            .collect(),
    )
}

/// Finds the span of a value in JSON text, so it can be replaced without reformatting the file.
struct Json<'a> {
    s: &'a [u8],
    i: usize,
}

impl<'a> Json<'a> {
    fn new(text: &'a str) -> Self {
        Json { s: text.as_bytes(), i: 0 }
    }

    /// Span of the value at `path` (object keys from the root).
    fn find(&mut self, path: &[&str]) -> Option<Range<usize>> {
        self.ws();
        let Some((key, rest)) = path.split_first() else {
            let start = self.i;
            self.skip_value()?;
            return Some(start..self.i);
        };
        self.expect(b'{')?;
        loop {
            self.ws();
            if self.peek()? == b'}' {
                return None;
            }
            let k = self.string()?;
            self.ws();
            self.expect(b':')?;
            if k == *key {
                return self.find(rest);
            }
            self.ws();
            self.skip_value()?;
            self.ws();
            if self.peek()? == b',' {
                self.i += 1;
            }
        }
    }

    fn peek(&self) -> Option<u8> {
        self.s.get(self.i).copied()
    }

    fn ws(&mut self) {
        while self.peek().is_some_and(|c| c.is_ascii_whitespace()) {
            self.i += 1;
        }
    }

    fn expect(&mut self, c: u8) -> Option<()> {
        (self.peek()? == c).then(|| self.i += 1)
    }

    fn string(&mut self) -> Option<String> {
        let start = self.i;
        self.expect(b'"')?;
        while self.peek()? != b'"' {
            self.i += if self.peek()? == b'\\' { 2 } else { 1 };
        }
        self.i += 1;
        serde_json::from_slice(&self.s[start..self.i]).ok()
    }

    fn skip_value(&mut self) -> Option<()> {
        match self.peek()? {
            b'"' => self.string().map(drop),
            open @ (b'{' | b'[') => {
                let close = if open == b'{' { b'}' } else { b']' };
                self.i += 1;
                loop {
                    self.ws();
                    match self.peek()? {
                        c if c == close => {
                            self.i += 1;
                            return Some(());
                        }
                        b',' | b':' => self.i += 1,
                        _ => self.skip_value()?,
                    }
                }
            }
            _ => {
                while self.peek().is_some_and(|c| !b",}] \t\r\n".contains(&c)) {
                    self.i += 1;
                }
                Some(())
            }
        }
    }
}

// ---------------------------------------------------------------- (Neo)Forge TOML

/// A `[[dependencies.<mod>]]` block of the mod itself, with the raw values.
struct TomlBlock {
    mod_id: String,
    version: String,
    /// Where the versionRange value is (inside the quotes).
    version_span: Option<Range<usize>>,
    /// End of the modId line, where a missing versionRange goes.
    insert_at: usize,
    required: bool,
}

fn toml_blocks(text: &str, gradle: &BTreeMap<String, String>) -> Vec<TomlBlock> {
    // (owner, block, type/mandatory)
    let mut blocks: Vec<(String, TomlBlock, Option<bool>)> = Vec::new();
    let mut main_id: Option<String> = None;
    enum In {
        Other,
        FirstMod,
        Dependency,
    }
    let mut current = In::Other;
    let mut offset = 0;
    let mut multiline: Option<&str> = None;
    for line in text.split_inclusive('\n') {
        let start = offset;
        offset += line.len();
        if let Some(quote) = multiline {
            if line.contains(quote) {
                multiline = None;
            }
            continue;
        }
        let content = strip_comment(line.trim_end_matches(['\r', '\n']));
        let trimmed = content.trim();
        if trimmed.starts_with("[[") && trimmed.ends_with("]]") {
            let table = trimmed[2..trimmed.len() - 2].trim();
            current = if table == "mods" && main_id.is_none() {
                In::FirstMod
            } else if let Some(owner) = table.strip_prefix("dependencies.") {
                let block = TomlBlock {
                    mod_id: String::new(),
                    version: "*".into(),
                    version_span: None,
                    insert_at: offset,
                    required: true,
                };
                blocks.push((owner.replace('"', ""), block, None));
                In::Dependency
            } else {
                In::Other
            };
            continue;
        }
        if trimmed.starts_with('[') {
            current = In::Other;
            continue;
        }
        let Some(eq) = content.find('=') else { continue };
        let key = content[..eq].trim();
        let raw = content[eq + 1..].trim();
        for quote in ["'''", "\"\"\""] {
            if raw.starts_with(quote) && !raw[3..].contains(quote) {
                multiline = Some(quote);
            }
        }
        let value = unquote(raw);
        match current {
            In::FirstMod if key == "modId" => main_id = Some(value),
            In::Dependency => {
                let Some((_, block, required)) = blocks.last_mut() else { continue };
                match key {
                    "modId" => {
                        block.mod_id = value;
                        block.insert_at = offset;
                    }
                    "versionRange" => {
                        let value_start =
                            start + eq + 1 + (content[eq + 1..].len() - content[eq + 1..].trim_start().len());
                        let quoted = raw.len() >= 2 && (raw.starts_with('"') || raw.starts_with('\''));
                        block.version_span = Some(if quoted {
                            value_start + 1..value_start + raw.len() - 1
                        } else {
                            value_start..value_start + raw.len()
                        });
                        block.version = value;
                    }
                    "type" => *required = Some(value.eq_ignore_ascii_case("required")),
                    "mandatory" if required.is_none() => *required = Some(value.eq_ignore_ascii_case("true")),
                    _ => {}
                }
            }
            _ => {}
        }
    }
    let main = main_id.map(|id| expand(&id, gradle));
    blocks
        .into_iter()
        .filter(|(owner, b, _)| !b.mod_id.is_empty() && main.as_deref().is_none_or(|m| expand(owner, gradle) == m))
        .map(|(_, mut b, required)| {
            b.required = required.unwrap_or(true);
            b
        })
        .collect()
}

// ---------------------------------------------------------------- suggestions

/// Versions offered for a dependency: placeholders of gradle.properties that belong to it, and their values.
struct Suggestions {
    gradle: BTreeMap<String, String>,
    kinds: Vec<(DependencyKind, String)>,
}

impl Suggestions {
    fn new(project: &Path, gradle: &BTreeMap<String, String>) -> Self {
        let kinds = dependencies::read(project).into_iter().filter_map(|e| Some((e.kind, e.key?))).collect();
        Suggestions { gradle: gradle.clone(), kinds }
    }

    fn of(&self, mod_id: &str, raw: &str) -> Vec<String> {
        use DependencyKind::*;
        let kind = match mod_id {
            "fabricloader" => Some(FabricLoader),
            "fabric-api" | "fabric" => Some(FabricApi),
            "neoforge" => Some(NeoForge),
            "forge" => Some(Forge),
            _ => None,
        };
        let normalized = |s: &str| s.chars().filter(char::is_ascii_alphanumeric).collect::<String>().to_lowercase();
        let id = normalized(mod_id);
        let keys: Vec<&String> = match (kind, mod_id) {
            (Some(k), _) => self.kinds.iter().filter(|(kind, _)| *kind == k).map(|(_, key)| key).collect(),
            (None, "minecraft") => ["minecraft_version", "mc_version", "minecraftVersion"]
                .iter()
                .filter_map(|k| self.gradle.get_key_value(*k).map(|(k, _)| k))
                .collect(),
            (None, "java") => self.gradle.keys().filter(|k| normalized(k) == "javaversion").collect(),
            _ if id.len() >= 3 => self.gradle.keys().filter(|k| normalized(k).starts_with(&id)).collect(),
            _ => Vec::new(),
        };
        let mut out: Vec<String> = PLACEHOLDER.find_iter(raw).map(|m| m.as_str().to_string()).collect();
        for key in keys {
            out.push(format!("${{{key}}}"));
            out.push(self.gradle[key].clone());
        }
        out.extend(PLACEHOLDER.find_iter(raw).map(|m| expand(m.as_str(), &self.gradle)).filter(|v| !v.contains("${")));
        let mut seen = std::collections::HashSet::new();
        out.retain(|v| seen.insert(v.clone()));
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const FABRIC: &str = r#"{
  "schemaVersion": 1,
  "id": "${mod_id}",
  "depends": {
    "fabricloader": ">=${fabric_loader}",
    "minecraft": ["~1.21", "1.21.1"],
    "fabric-api": "*"
  },
  "suggests": { "modmenu": "*" }
}
"#;
    const NEOFORGE: &str = "modLoader = \"javafml\" #mandatory\nloaderVersion = \"[4,)\"\n\n[[mods]]\n    modId = \"${mod_id}\"\n\
        description = '''\n[[dependencies.fake]]\nmodId = \"x\"'''\n\n[[dependencies.${mod_id}]]\n    modId = \"neoforge\"\n\
        type=\"required\"\n    versionRange = \"[${neoforge_version},)\" # comment\n    ordering = \"NONE\"\n\n\
        [[dependencies.${mod_id}]]\n    modId = \"jei\"\n    type=\"optional\"\n";

    fn project() -> tempfile::TempDir {
        let tmp = tempfile::tempdir().unwrap();
        let p = tmp.path();
        fs::write(
            p.join("gradle.properties"),
            "minecraft_version=26.1\nmod_id=demo\nfabric_loader=0.19.5\nneoforge=26.1.0.19-beta\nmodmenu_version=17.0.0\n",
        )
        .unwrap();
        fs::create_dir_all(p.join("fabric/src/main/resources")).unwrap();
        fs::write(p.join("fabric/src/main/resources/fabric.mod.json"), FABRIC).unwrap();
        fs::create_dir_all(p.join("neoforge/src/main/resources/META-INF")).unwrap();
        fs::write(p.join("neoforge/src/main/resources/META-INF/neoforge.mods.toml"), NEOFORGE).unwrap();
        fs::create_dir_all(p.join("common/src/main/resources")).unwrap();
        tmp
    }

    #[test]
    fn reads_the_files_of_all_loaders() {
        let tmp = project();
        let files = read(tmp.path());
        assert_eq!(
            files.iter().map(|f| f.file.as_str()).collect::<Vec<_>>(),
            ["fabric/src/main/resources/fabric.mod.json", "neoforge/src/main/resources/META-INF/neoforge.mods.toml"]
        );
        let fabric = &files[0].dependencies;
        assert_eq!(
            fabric.iter().map(|d| d.mod_id.as_str()).collect::<Vec<_>>(),
            ["fabricloader", "minecraft", "fabric-api", "modmenu"]
        );
        assert_eq!(fabric[0].constraint, VersionConstraint::AtLeast("${fabric_loader}".into()));
        assert_eq!(fabric[0].resolved, ">=0.19.5");
        assert_eq!(fabric[0].suggestions, ["${fabric_loader}", "0.19.5"]);
        assert_eq!(fabric[1].constraint, VersionConstraint::Custom("~1.21 || 1.21.1".into()));
        assert!(!fabric[3].required);
        assert_eq!(fabric[3].suggestions, ["${modmenu_version}", "17.0.0"]);
        let neo = &files[1].dependencies;
        assert_eq!(neo.len(), 2);
        assert_eq!(neo[0].resolved, "[26.1.0.19-beta,)");
        assert_eq!(neo[0].suggestions, ["${neoforge_version}", "${neoforge}", "26.1.0.19-beta"]);
        assert_eq!(neo[1].constraint, VersionConstraint::Any);
        assert!(!neo[1].required);
    }

    #[test]
    fn changes_declarations_in_place() {
        let tmp = project();
        let p = tmp.path();
        let fabric = "fabric/src/main/resources/fabric.mod.json";
        let old = set(p, fabric, "fabricloader", &VersionConstraint::Exact("${fabric_loader}".into())).unwrap();
        assert_eq!(old, ">=${fabric_loader}");
        let range = VersionConstraint::Range { min: "1.21".into(), max: "1.22".into(), max_inclusive: false };
        set(p, fabric, "minecraft", &range).unwrap();
        set(p, fabric, "modmenu", &VersionConstraint::AtLeast("17.0".into())).unwrap();
        assert_eq!(
            fs::read_to_string(p.join(fabric)).unwrap(),
            FABRIC
                .replace(">=${fabric_loader}", "${fabric_loader}")
                .replace(r#"["~1.21", "1.21.1"]"#, r#"">=1.21 <1.22""#)
                .replace(r#""modmenu": "*""#, r#""modmenu": ">=17.0""#)
        );

        let neo = "neoforge/src/main/resources/META-INF/neoforge.mods.toml";
        set(p, neo, "neoforge", &VersionConstraint::Exact("26.1.0.20".into())).unwrap();
        set(p, neo, "jei", &range).unwrap();
        assert_eq!(
            fs::read_to_string(p.join(neo)).unwrap(),
            NEOFORGE
                .replace("[${neoforge_version},)", "[26.1.0.20]")
                .replace("    modId = \"jei\"\n", "    modId = \"jei\"\n    versionRange = \"[1.21,1.22)\"\n")
        );
        assert!(set(p, "../x/fabric.mod.json", "a", &range).is_err());
        assert!(set(p, fabric, "unknown", &range).is_err());
    }
}
