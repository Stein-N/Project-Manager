//! Finds a project's README and renders it (GitHub flavored Markdown) to sanitized HTML. Local images are embedded
//! as data URLs and relative links point to the files, so the UI needs no file access.

use crate::util;
use anyhow::Result;
use std::borrow::Cow;
use std::path::{Path, PathBuf};

const NAMES: [&str; 4] = ["readme.md", "readme.markdown", "readme.txt", "readme"];

/// README in the project root (case-insensitive; Markdown preferred).
pub fn find(project: &Path) -> Option<PathBuf> {
    let files: Vec<PathBuf> =
        std::fs::read_dir(project).ok()?.flatten().map(|e| e.path()).filter(|p| p.is_file()).collect();
    NAMES.iter().find_map(|name| files.iter().find(|f| util::file_name(f).to_lowercase() == *name).cloned())
}

/// The README as HTML (body content only; the UI styles it).
pub fn render(file: &Path) -> Result<String> {
    let text = String::from_utf8_lossy(&std::fs::read(file)?).into_owned();
    let name = util::file_name(file).to_lowercase();
    let base = file.parent().map(Path::to_path_buf).unwrap_or_default();
    Ok(if name.ends_with(".md") || name.ends_with(".markdown") {
        markdown_to_safe_html(&text, &base)
    } else {
        format!("<pre>{}</pre>", escape(&text))
    })
}

/// Markdown (README, changelog drafts, Claude's notes) as sanitized HTML; relative paths resolve against `base`.
pub fn markdown_to_safe_html(markdown: &str, base: &Path) -> String {
    sanitize(&markdown_to_html(markdown), base)
}

fn markdown_to_html(markdown: &str) -> String {
    let mut options = comrak::Options::default();
    let e = &mut options.extension;
    e.table = true;
    e.strikethrough = true;
    e.tasklist = true;
    e.autolink = true;
    e.footnotes = true;
    e.header_id_prefix = Some(String::new());
    options.render.r#unsafe = true; // raw HTML (badges, <details>, centered logos) is sanitized afterwards
    comrak::markdown_to_html(markdown, &options)
}

/// Removes scripts and other active content; relative image sources become data URLs, relative links `file:` URLs.
fn sanitize(html: &str, base: &Path) -> String {
    let base = base.to_path_buf();
    ammonia::Builder::default()
        .add_tags(["input", "details", "summary", "picture", "source", "section"])
        .add_generic_attributes(["id", "class", "align", "width", "height"])
        .add_tag_attributes("input", ["type", "checked", "disabled"])
        .add_tag_attributes("source", ["srcset", "media"])
        .add_tag_attributes("a", ["data-heading-content", "aria-label"])
        .link_rel(None)
        .attribute_filter(move |element, attribute, value| {
            if !is_relative(value) {
                return Some(Cow::Borrowed(value));
            }
            match (element, attribute) {
                ("img", "src") => Some(local_image(&base, value).map(Cow::Owned).unwrap_or(Cow::Borrowed(value))),
                ("a", "href") if !value.starts_with('#') => Some(Cow::Owned(file_url(&base.join(strip_query(value))))),
                _ => Some(Cow::Borrowed(value)),
            }
        })
        .clean(html)
        .to_string()
}

fn is_relative(url: &str) -> bool {
    !(url.contains("://") || url.starts_with("//") || url.starts_with("data:") || url.starts_with("mailto:"))
}

fn strip_query(url: &str) -> &str {
    url.split(['?', '#']).next().unwrap_or(url)
}

fn local_image(base: &Path, url: &str) -> Option<String> {
    let path = base.join(percent_decode(strip_query(url)).trim_start_matches("./"));
    util::image_data_url(&path)
}

fn file_url(path: &Path) -> String {
    let p = util::absolute(path).to_string_lossy().replace('\\', "/");
    if p.starts_with('/') { format!("file://{p}") } else { format!("file:///{p}") }
}

fn percent_decode(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%'
            && i + 2 < bytes.len()
            && let Ok(b) = u8::from_str_radix(&s[i + 1..i + 3], 16)
        {
            out.push(b);
            i += 3;
            continue;
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

fn escape(text: &str) -> String {
    text.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;")
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn finds_readme_case_insensitive() {
        let tmp = tempfile::tempdir().unwrap();
        assert_eq!(find(tmp.path()), None);
        fs::write(tmp.path().join("ReadMe.MD"), "# Hi").unwrap();
        assert_eq!(util::file_name(&find(tmp.path()).unwrap()), "ReadMe.MD");
    }

    #[test]
    fn renders_github_flavored_markdown_safely() {
        let tmp = tempfile::tempdir().unwrap();
        fs::create_dir_all(tmp.path().join("docs")).unwrap();
        fs::write(tmp.path().join("docs/logo.png"), [137, 80, 78, 71]).unwrap();
        let file = tmp.path().join("README.md");
        fs::write(&file, "# Title\n[![Downloads](https://img.shields.io/badge/x.svg)](https://modrinth.com/mod/x)\n\n\
            ![logo](docs/logo.png) [License](LICENSE)\n\n| Loader | Version |\n|--------|---------|\n| Fabric | 1.21.1  |\n\n\
            - [x] done\n- [ ] open\n\n~~old~~ and https://example.com and a note[^1]\n\n[^1]: The footnote.\n\n\
            <script>alert(1)</script><img src=\"x\" onerror=\"alert(2)\">\n").unwrap();
        let html = render(&file).unwrap();
        assert!(html.contains("id=\"title\""), "heading anchor: {html}");
        assert!(html.contains("src=\"https://img.shields.io/badge/x.svg\""), "badges are kept");
        assert!(html.contains("src=\"data:image/png;base64,"), "local image embedded");
        assert!(html.contains("href=\"file://"), "relative link points to the file");
        assert!(html.contains("<table>"));
        assert!(html.contains("type=\"checkbox\""), "task list");
        assert!(html.contains("<del>old</del>"));
        assert!(html.contains("href=\"https://example.com\""), "autolink");
        assert!(html.contains("The footnote."));
        assert!(!html.contains("<script") && !html.contains("onerror"), "sanitized: {html}");
    }
}
