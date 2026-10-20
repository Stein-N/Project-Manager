//! Mod metadata from fabric.mod.json, quilt.mod.json, neoforge.mods.toml or mods.toml. Placeholders like
//! `${mod_id}` are resolved from gradle.properties.

use pm_types::{ModDependency, ModMetadata};
use regex::Regex;
use serde_json::Value;
use std::collections::BTreeMap;
use std::fs;
use std::path::Path;
use std::sync::LazyLock;

const SUBPROJECTS: [&str; 6] = ["", "fabric", "neoforge", "forge", "quilt", "common"];
static PLACEHOLDER: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\$\{([A-Za-z0-9_.]+)}").unwrap());

pub fn read(project: &Path, gradle: &BTreeMap<String, String>) -> Option<ModMetadata> {
    for sub in SUBPROJECTS {
        let base = if sub.is_empty() { project.to_path_buf() } else { project.join(sub) };
        let resources = base.join("src/main/resources");
        if !resources.is_dir() {
            continue;
        }
        // broken or templated beyond recognition – try the next location
        let found = if resources.join("fabric.mod.json").is_file() {
            fabric(project, &resources, &resources.join("fabric.mod.json"), gradle)
        } else if resources.join("quilt.mod.json").is_file() {
            quilt(project, &resources, &resources.join("quilt.mod.json"), gradle)
        } else {
            ["META-INF/neoforge.mods.toml", "META-INF/mods.toml"]
                .iter()
                .map(|t| resources.join(t))
                .find(|f| f.is_file())
                .and_then(|f| toml(project, &resources, &f, gradle))
        };
        if found.is_some() {
            return found;
        }
    }
    None
}

fn relative(project: &Path, file: &Path) -> String {
    file.strip_prefix(project).unwrap_or(file).to_string_lossy().into_owned()
}

// ---------------------------------------------------------------- Fabric / Quilt

fn fabric(project: &Path, resources: &Path, file: &Path, gradle: &BTreeMap<String, String>) -> Option<ModMetadata> {
    let json: Value = serde_json::from_str(&expand(&fs::read_to_string(file).ok()?, gradle)).ok()?;
    let authors = json["authors"]
        .as_array()
        .map(|a| a.iter().filter_map(|x| x.as_str().or_else(|| x["name"].as_str()).map(str::to_string)).collect())
        .unwrap_or_default();
    let mut deps = Vec::new();
    fabric_deps(&json["depends"], true, &mut deps);
    fabric_deps(&json["recommends"], false, &mut deps);
    Some(ModMetadata {
        id: str(&json["id"]),
        name: str(&json["name"]),
        version: str(&json["version"]),
        description: str(&json["description"]),
        authors,
        icon: icon(project, resources, icon_path(&json["icon"]).as_deref()),
        dependencies: deps,
        file: relative(project, file),
    })
}

fn fabric_deps(value: &Value, required: bool, out: &mut Vec<ModDependency>) {
    let Some(map) = value.as_object() else { return };
    for (id, v) in map {
        let version = match v {
            Value::Array(a) => a.iter().filter_map(Value::as_str).collect::<Vec<_>>().join(" || "),
            other => other.as_str().unwrap_or("*").to_string(),
        };
        out.push(ModDependency { mod_id: Some(id.clone()), version, required });
    }
}

fn quilt(project: &Path, resources: &Path, file: &Path, gradle: &BTreeMap<String, String>) -> Option<ModMetadata> {
    let root: Value = serde_json::from_str(&expand(&fs::read_to_string(file).ok()?, gradle)).ok()?;
    let loader = root.get("quilt_loader")?;
    let meta = &loader["metadata"];
    let mut deps = Vec::new();
    for d in loader["depends"].as_array().into_iter().flatten() {
        match d {
            Value::Object(o) => deps.push(ModDependency {
                mod_id: o.get("id").and_then(str),
                version: o.get("versions").map(|v| v.to_string().replace('"', "")).unwrap_or_else(|| "*".into()),
                required: !o.get("optional").and_then(Value::as_bool).unwrap_or(false),
            }),
            other => deps.push(ModDependency { mod_id: str(other), version: "*".into(), required: true }),
        }
    }
    let authors = meta["contributors"].as_object().map(|o| o.keys().cloned().collect()).unwrap_or_default();
    Some(ModMetadata {
        id: str(&loader["id"]),
        name: str(&meta["name"]),
        version: str(&loader["version"]),
        description: str(&meta["description"]),
        authors,
        icon: icon(project, resources, icon_path(&meta["icon"]).as_deref()),
        dependencies: deps,
        file: relative(project, file),
    })
}

/// "icon" is either a path or an object `{"16": "...", "128": "..."}` – take the largest.
fn icon_path(icon: &Value) -> Option<String> {
    match icon {
        Value::String(s) => Some(s.clone()),
        Value::Object(o) => o
            .iter()
            .max_by_key(|(size, _)| size.trim().parse::<u32>().unwrap_or(0))
            .and_then(|(_, v)| v.as_str().map(str::to_string)),
        _ => None,
    }
}

// ---------------------------------------------------------------- (Neo)Forge TOML

fn toml(project: &Path, resources: &Path, file: &Path, gradle: &BTreeMap<String, String>) -> Option<ModMetadata> {
    type Table = BTreeMap<String, String>;
    let mut mods: Vec<Table> = Vec::new();
    let mut deps: Vec<(String, Table)> = Vec::new();
    // which table the following keys belong to
    enum Current {
        Ignored,
        Mod,
        Dependency,
    }
    let mut current = Current::Ignored;
    let text = expand(&fs::read_to_string(file).ok()?, gradle);
    let lines: Vec<&str> = text.lines().collect();
    let mut i = 0;
    while i < lines.len() {
        let line = strip_comment(lines[i]).trim().to_string();
        i += 1;
        if line.is_empty() {
            continue;
        }
        if line.starts_with("[[") && line.ends_with("]]") {
            let table = line[2..line.len() - 2].trim();
            current = if table == "mods" {
                mods.push(Table::new());
                Current::Mod
            } else if let Some(owner) = table.strip_prefix("dependencies.") {
                deps.push((owner.replace('"', ""), Table::new()));
                Current::Dependency
            } else {
                Current::Ignored
            };
            continue;
        }
        if line.starts_with('[') {
            current = Current::Ignored;
            continue;
        }
        let Some(eq) = line.find('=').filter(|&e| e > 0) else { continue };
        let key = line[..eq].trim().to_string();
        let mut value = line[eq + 1..].trim().to_string();
        if value.starts_with("'''") || value.starts_with("\"\"\"") {
            let quote = value[..3].to_string();
            let mut sb = value[3..].to_string();
            while !sb.contains(&quote) && i < lines.len() {
                sb.push('\n');
                sb.push_str(lines[i]);
                i += 1;
            }
            value = sb.find(&quote).map(|end| sb[..end].to_string()).unwrap_or(sb);
        } else {
            value = unquote(&value);
        }
        let target = match current {
            Current::Mod => mods.last_mut(),
            Current::Dependency => deps.last_mut().map(|(_, t)| t),
            Current::Ignored => None,
        };
        if let Some(t) = target {
            t.insert(key, value.trim().to_string());
        }
    }
    let empty = Table::new();
    let m = mods.first().unwrap_or(&empty);
    let id = m.get("modId").cloned();
    let dependencies = deps
        .iter()
        .filter(|(owner, _)| Some(owner) == id.as_ref())
        .map(|(_, d)| ModDependency {
            mod_id: d.get("modId").cloned(),
            version: d.get("versionRange").cloned().unwrap_or_else(|| "*".into()),
            required: match d.get("type") {
                Some(t) => t.eq_ignore_ascii_case("required"),
                None => d.get("mandatory").map(|v| v.eq_ignore_ascii_case("true")).unwrap_or(true),
            },
        })
        .collect();
    Some(ModMetadata {
        id,
        name: m.get("displayName").cloned(),
        version: m.get("version").cloned(),
        description: m.get("description").cloned(),
        authors: m.get("authors").cloned().into_iter().collect(),
        icon: icon(project, resources, m.get("logoFile").map(String::as_str)),
        dependencies,
        file: relative(project, file),
    })
}

pub(crate) fn strip_comment(line: &str) -> &str {
    let mut quote = None;
    for (i, c) in line.char_indices() {
        match quote {
            Some(q) if c == q => quote = None,
            Some(_) => {}
            None if c == '"' || c == '\'' => quote = Some(c),
            None if c == '#' => return &line[..i],
            None => {}
        }
    }
    line
}

pub(crate) fn unquote(value: &str) -> String {
    let quoted = value.len() >= 2
        && ((value.starts_with('"') && value.ends_with('"')) || (value.starts_with('\'') && value.ends_with('\'')));
    if quoted { value[1..value.len() - 1].to_string() } else { value.to_string() }
}

// ---------------------------------------------------------------- helpers

/// Replaces `${key}` with values from gradle.properties ("version" falls back to mod_version).
pub fn expand(text: &str, gradle: &BTreeMap<String, String>) -> String {
    PLACEHOLDER
        .replace_all(text, |c: &regex::Captures| {
            let key = &c[1];
            gradle
                .get(key)
                .or_else(|| if key == "version" || key == "file.jarVersion" { gradle.get("mod_version") } else { None })
                // builds that map `neoforge` to `${neoforge_version}` (or the other way round)
                .or_else(|| key.strip_suffix("_version").and_then(|k| gradle.get(k)))
                .or_else(|| gradle.get(&format!("{key}_version")))
                .cloned()
                .unwrap_or_else(|| c[0].to_string())
        })
        .into_owned()
}

/// The icon next to the mod file, or – in multiloader projects, where Gradle merges the resources of `common` into
/// each loader – in the resources of another subproject.
fn icon(project: &Path, resources: &Path, relative: Option<&str>) -> Option<std::path::PathBuf> {
    let rel = relative.filter(|r| !r.trim().is_empty() && !r.contains("${"))?.replace('\\', "/");
    let rel = rel.trim_start_matches('/');
    std::iter::once(resources.to_path_buf()).chain(resource_dirs(project)).map(|r| r.join(rel)).find(|i| i.is_file())
}

/// `src/main/resources` of the project and of its direct subprojects: the root first, then `common`, then the others
/// alphabetically.
pub(crate) fn resource_dirs(project: &Path) -> Vec<std::path::PathBuf> {
    let mut subs: Vec<_> = fs::read_dir(project)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| e.path())
        .filter(|p| {
            let name = p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            !name.starts_with('.') && name != "build" && p.is_dir()
        })
        .collect();
    subs.sort_by_key(|p| (!p.ends_with("common"), p.clone()));
    std::iter::once(project.to_path_buf())
        .chain(subs)
        .map(|b| b.join("src/main/resources"))
        .filter(|r| r.is_dir())
        .collect()
}

fn str(v: &Value) -> Option<String> {
    match v {
        Value::String(s) => Some(s.clone()),
        Value::Number(n) => Some(n.to_string()),
        Value::Bool(b) => Some(b.to_string()),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn props(pairs: &[(&str, &str)]) -> BTreeMap<String, String> {
        pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect()
    }

    #[test]
    fn reads_fabric_mod_json_with_placeholders_and_icon_object() {
        let tmp = tempfile::tempdir().unwrap();
        let res = tmp.path().join("src/main/resources");
        fs::create_dir_all(res.join("assets/coolmod")).unwrap();
        fs::write(res.join("assets/coolmod/icon.png"), [1]).unwrap();
        fs::write(
            res.join("fabric.mod.json"),
            r#"{
          "schemaVersion": 1, "id": "${mod_id}", "version": "${version}", "name": "Cool Mod",
          "authors": ["Nico", {"name": "Helper"}],
          "icon": {"16": "assets/coolmod/small.png", "128": "assets/${mod_id}/icon.png"},
          "depends": {"fabricloader": ">=0.16", "minecraft": ["~1.21", "1.21.1"]},
          "recommends": {"modmenu": "*"}
        }"#,
        )
        .unwrap();
        let m = read(tmp.path(), &props(&[("mod_id", "coolmod"), ("mod_version", "1.2.3")])).unwrap();
        assert_eq!(m.id.as_deref(), Some("coolmod"));
        assert_eq!(m.version.as_deref(), Some("1.2.3"));
        assert_eq!(m.authors, ["Nico", "Helper"]);
        assert!(m.icon.is_some());
        assert_eq!(m.dependencies.len(), 3);
        assert_eq!(m.dependencies[1].version, "~1.21 || 1.21.1");
        assert!(!m.dependencies[2].required);
    }

    #[test]
    fn reads_neoforge_toml_in_subproject() {
        let tmp = tempfile::tempdir().unwrap();
        let res = tmp.path().join("neoforge/src/main/resources/META-INF");
        fs::create_dir_all(&res).unwrap();
        fs::write(res.join("neoforge.mods.toml"), "modLoader=\"javafml\" # comment\nloaderVersion=\"${loader_version_range}\"\n\n\
            [[mods]]\nmodId=\"${mod_id}\"\nversion=\"${mod_version}\"\ndisplayName=\"Example # not a comment\"\nauthors=\"Nico\"\n\
            description='''\nFirst line\nsecond line'''\n\n[[dependencies.${mod_id}]]\n    modId=\"neoforge\"\n    type=\"required\"\n\
                versionRange=\"[21.1,)\"\n\n[[dependencies.${mod_id}]]\n    modId=\"jei\"\n    type=\"optional\"\n    versionRange=\"*\"\n").unwrap();
        let m = read(tmp.path(), &props(&[("mod_id", "examplemod"), ("mod_version", "2.0")])).unwrap();
        assert_eq!(m.id.as_deref(), Some("examplemod"));
        assert_eq!(m.name.as_deref(), Some("Example # not a comment"));
        assert_eq!(m.description.as_deref(), Some("First line\nsecond line"));
        assert_eq!(m.dependencies.len(), 2);
        assert!(m.dependencies[0].required);
        assert!(!m.dependencies[1].required);
        assert!(m.file.replace('\\', "/").ends_with("neoforge/src/main/resources/META-INF/neoforge.mods.toml"));
    }

    #[test]
    fn icon_from_common_resources() {
        let tmp = tempfile::tempdir().unwrap();
        let p = tmp.path();
        fs::create_dir_all(p.join("fabric/src/main/resources")).unwrap();
        fs::write(
            p.join("fabric/src/main/resources/fabric.mod.json"),
            r#"{"id": "${mod_id}", "icon": "${mod_id}.png"}"#,
        )
        .unwrap();
        fs::create_dir_all(p.join("common/src/main/resources")).unwrap();
        fs::write(p.join("common/src/main/resources/demo.png"), [1]).unwrap();
        let m = read(p, &props(&[("mod_id", "demo")])).unwrap();
        assert_eq!(m.icon, Some(p.join("common/src/main/resources/demo.png")));
    }

    #[test]
    fn forge_mandatory_flag() {
        let tmp = tempfile::tempdir().unwrap();
        let res = tmp.path().join("src/main/resources/META-INF");
        fs::create_dir_all(&res).unwrap();
        fs::write(
            res.join("mods.toml"),
            "[[mods]]\nmodId=\"old\"\n[[dependencies.old]]\n    modId=\"forge\"\n    mandatory=true\n\
            [[dependencies.old]]\n    modId=\"curios\"\n    mandatory=false\n",
        )
        .unwrap();
        let m = read(tmp.path(), &BTreeMap::new()).unwrap();
        assert!(m.dependencies[0].required);
        assert!(!m.dependencies[1].required);
    }
}
