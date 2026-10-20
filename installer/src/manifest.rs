//! The release manifest published with every release, and checked downloads.

use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};

pub const REPO: &str = "ojasookert/shroom_and_gloom_vr";
const SCHEMA: u32 = 1;

#[derive(Debug, Clone, Deserialize)]
pub struct Manifest {
    pub schema: u32,
    pub version: String,
    pub package: Download,
    pub bepinex: BepInEx,
    /// Game builds this release was tested with.
    pub game_versions: Vec<GameVersion>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct Download {
    pub url: String,
    pub sha256: String,
    #[serde(default)]
    pub size: u64,
}

#[derive(Debug, Clone, Deserialize)]
pub struct BepInEx {
    pub version: String,
    pub url: String,
    pub sha256: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct GameVersion {
    pub version: String,
    #[serde(default)]
    pub steam_build: Option<u64>,
    pub gameassembly_sha256: String,
}

impl Manifest {
    /// The tested game version matching this game install, if any.
    pub fn tested(&self, gameassembly_sha256: &str) -> Option<&GameVersion> {
        self.game_versions.iter().find(|g| {
            g.gameassembly_sha256
                .eq_ignore_ascii_case(gameassembly_sha256)
        })
    }
}

/// `SHROOMVR_MANIFEST` points at a local file or URL instead of the latest release (for testing builds).
pub fn fetch() -> Result<Manifest, String> {
    let json = match std::env::var("SHROOMVR_MANIFEST") {
        Ok(source) if !source.starts_with("https://") => {
            fs::read_to_string(&source).map_err(|e| format!("Can't read {source}: {e}"))?
        }
        Ok(url) => get_string(&url)?,
        Err(_) => get_string(&format!(
            "https://github.com/{REPO}/releases/latest/download/manifest.json"
        ))?,
    };
    let manifest: Manifest = serde_json::from_str(&json)
        .map_err(|e| format!("The release manifest is malformed: {e}"))?;
    if manifest.schema != SCHEMA {
        return Err(format!(
            "This installer is too old for the latest release. Download the new one from https://github.com/{REPO}/releases"
        ));
    }
    Ok(manifest)
}

fn get_string(url: &str) -> Result<String, String> {
    ureq::get(url)
        .header("User-Agent", "shroomvr-installer")
        .call()
        .map_err(|e| format!("Couldn't reach GitHub ({e}). Check your connection and try again."))?
        .body_mut()
        .read_to_string()
        .map_err(|e| format!("Download failed: {e}"))
}

/// Downloads a file and checks its SHA-256, reusing an earlier download with the same hash.
/// Local paths are accepted for testing builds.
pub fn download(url: &str, sha256: &str, progress: &dyn Fn(f32)) -> Result<Vec<u8>, String> {
    let cached = cache_dir().join(format!("{}.zip", sha256.to_ascii_lowercase()));
    if let Ok(bytes) = fs::read(&cached)
        && hex_sha256(&bytes).eq_ignore_ascii_case(sha256)
    {
        progress(1.0);
        return Ok(bytes);
    }
    let bytes = if url.starts_with("https://") {
        let mut response = ureq::get(url)
            .header("User-Agent", "shroomvr-installer")
            .call()
            .map_err(|e| format!("Download of {url} failed: {e}"))?;
        let total = response.body().content_length().unwrap_or(0);
        let mut reader = response.body_mut().as_reader();
        let mut bytes = Vec::with_capacity(total as usize);
        let mut chunk = [0u8; 64 * 1024];
        loop {
            let n = reader
                .read(&mut chunk)
                .map_err(|e| format!("Download of {url} failed: {e}"))?;
            if n == 0 {
                break;
            }
            bytes.extend_from_slice(&chunk[..n]);
            if total > 0 {
                progress(bytes.len() as f32 / total as f32);
            }
        }
        bytes
    } else {
        fs::read(url).map_err(|e| format!("Can't read {url}: {e}"))?
    };
    let actual = hex_sha256(&bytes);
    if !actual.eq_ignore_ascii_case(sha256) {
        return Err(format!(
            "{url} didn't match its checksum (got {actual}). Try again later."
        ));
    }
    let _ = fs::create_dir_all(cache_dir());
    let _ = fs::write(&cached, &bytes);
    progress(1.0);
    Ok(bytes)
}

pub fn hex_sha256(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

pub fn file_sha256(path: &Path) -> std::io::Result<String> {
    let mut file = fs::File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buf = vec![0u8; 1 << 20];
    loop {
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(hasher
        .finalize()
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect())
}

pub fn cache_dir() -> PathBuf {
    std::env::var_os("XDG_CACHE_HOME")
        .map(PathBuf::from)
        .unwrap_or_else(|| crate::steam::home().join(".cache"))
        .join("shroomvr-installer")
}

/// Orders dotted version numbers ("0.0.10" > "0.0.9"); anything after '-' is ignored.
pub fn newer(a: &str, b: &str) -> bool {
    let parse = |v: &str| -> Vec<u64> {
        v.trim_start_matches('v')
            .split('-')
            .next()
            .unwrap_or("")
            .split('.')
            .map(|p| p.parse().unwrap_or(0))
            .collect()
    };
    parse(a) > parse(b)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn versions() {
        assert!(newer("0.0.10", "0.0.9"));
        assert!(newer("v0.1.0", "0.0.99"));
        assert!(!newer("0.0.1", "0.0.1"));
        assert!(!newer("0.0.0-dev", "0.0.1"));
    }

    #[test]
    fn sha() {
        assert_eq!(
            hex_sha256(b"abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }
}
