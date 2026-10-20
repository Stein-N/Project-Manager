//! HTTP GET for the version lookups, the GitHub API and the update download.

use anyhow::{Context, Result};
use std::io::Read;
use std::path::Path;
use std::sync::LazyLock;
use std::time::Duration;

const USER_AGENT: &str = "MC-Mod-Project-Manager";

static AGENT: LazyLock<ureq::Agent> = LazyLock::new(|| {
    ureq::Agent::config_builder()
        .http_status_as_error(false)
        .timeout_connect(Some(Duration::from_secs(10)))
        .timeout_global(Some(Duration::from_secs(60)))
        .build()
        .new_agent()
});

pub struct Response {
    pub status: u16,
    pub body: String,
    /// The `Link` header (GitHub pagination).
    pub link: Option<String>,
}

pub fn get(url: &str, headers: &[(&str, &str)]) -> Result<Response> {
    let mut request = AGENT.get(url).header("User-Agent", USER_AGENT);
    for (k, v) in headers {
        request = request.header(*k, *v);
    }
    let mut response = request.call().with_context(|| format!("Request failed ({url})"))?;
    let status = response.status().as_u16();
    let link = response.headers().get("link").and_then(|v| v.to_str().ok()).map(str::to_string);
    let body = response
        .body_mut()
        .with_config()
        .limit(50 * 1024 * 1024)
        .read_to_string()
        .with_context(|| format!("Could not read the answer of {url}"))?;
    Ok(Response { status, body, link })
}

/// The body of a successful request; an error for anything but 200.
pub fn get_ok(url: &str) -> Result<String> {
    let r = get(url, &[])?;
    match r.status {
        200 => Ok(r.body),
        404 => anyhow::bail!("not found ({url})"),
        s => anyhow::bail!("HTTP {s} ({url})"),
    }
}

/// Downloads `url` into `file`.
pub fn download(url: &str, file: &Path) -> Result<()> {
    let response = AGENT
        .get(url)
        .header("User-Agent", USER_AGENT)
        .config()
        .timeout_global(Some(Duration::from_secs(600)))
        .build()
        .call()
        .with_context(|| format!("Download failed ({url})"))?;
    if response.status() != 200 {
        anyhow::bail!("Download failed: HTTP {}", response.status());
    }
    let mut reader = response.into_body().into_reader();
    let mut out = std::fs::File::create(file)?;
    let mut buf = vec![0u8; 64 * 1024];
    loop {
        let n = reader.read(&mut buf)?;
        if n == 0 {
            break;
        }
        std::io::Write::write_all(&mut out, &buf[..n])?;
    }
    Ok(())
}
