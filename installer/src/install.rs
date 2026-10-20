//! Installing, updating and removing ShroomVR.
//!
//! Every install records the files it wrote in `<game>/.shroomvr/install.json`. Updates delete files
//! the previous version shipped that the new one doesn't, and uninstall removes exactly what was
//! recorded. Game files that get replaced are backed up first and put back on uninstall. BepInEx is
//! only removed again if the installer put it there and no other mods are using it.

use crate::art::Art;
use crate::manifest::{self, Manifest};
use crate::steam::{self, APP_ID, GAME_EXE, Steam};
use crate::vdf::{self, Bin};
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::fs;
use std::io::{Cursor, Read};
use std::os::unix::fs::PermissionsExt;
use std::path::{Component, Path, PathBuf};

pub const SHORTCUT_NAME: &str = "Shroom and Gloom VR";
const RECORD_DIR: &str = ".shroomvr";
const PLUGIN_DIR: &str = "BepInEx/plugins/ShroomVR";
const CONFIG_FILE: &str = "BepInEx/config/com.shroomvr.shroomandgloom.cfg";
const BEPINEX_ROOT_FILES: &[&str] = &[
    "winhttp.dll",
    "doorstop_config.ini",
    ".doorstop_version",
    "changelog.txt",
];

#[derive(Debug, Default, Clone, Serialize, Deserialize)]
pub struct Record {
    pub version: String,
    pub files: Vec<String>,
    /// Game files the mod replaced, saved under .shroomvr/backup.
    #[serde(default)]
    pub backups: Vec<String>,
    /// Set when the installer put BepInEx there.
    #[serde(default)]
    pub bepinex: Option<BepInExRecord>,
}

#[derive(Debug, Default, Clone, Serialize, Deserialize)]
pub struct BepInExRecord {
    pub version: String,
    pub files: Vec<String>,
}

pub fn read_record(game: &Path) -> Option<Record> {
    let text = fs::read_to_string(game.join(RECORD_DIR).join("install.json")).ok()?;
    serde_json::from_str(&text).ok()
}

fn write_record(game: &Path, record: &Record) -> Result<(), String> {
    let dir = game.join(RECORD_DIR);
    fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let json = serde_json::to_string_pretty(record).map_err(|e| e.to_string())?;
    fs::write(dir.join("install.json"), json)
        .map_err(|e| format!("Can't write the install record: {e}"))
}

pub fn bepinex_present(game: &Path) -> bool {
    game.join("winhttp.dll").is_file()
        && game.join("BepInEx/core/BepInEx.Unity.IL2CPP.dll").is_file()
}

/// Progress callback: a status line and, when known, a fraction for the current step.
pub type Progress<'a> = &'a dyn Fn(&str, Option<f32>);

pub struct Options {
    pub steam_entry: bool,
}

/// Installs or updates to the manifest's release. Steam must not be running when `steam_entry` is
/// set (it rewrites its shortcuts when it exits); callers close it first.
pub fn install(
    steam: &Steam,
    game: &Path,
    manifest: &Manifest,
    options: &Options,
    progress: Progress,
) -> Result<(), String> {
    if steam::game_running() {
        return Err("Close Shroom and Gloom first.".into());
    }
    let previous = read_record(game);
    let mut record = Record {
        version: manifest.version.clone(),
        files: Vec::new(),
        backups: previous
            .as_ref()
            .map(|r| r.backups.clone())
            .unwrap_or_default(),
        bepinex: previous.as_ref().and_then(|r| r.bepinex.clone()),
    };

    // BepInEx: install it unless some other install of it is already there.
    let ours = record.bepinex.as_ref();
    let outdated = ours.is_some_and(|b| b.version != manifest.bepinex.version);
    if !bepinex_present(game) || outdated {
        progress(
            &format!("Downloading BepInEx {}", manifest.bepinex.version),
            Some(0.0),
        );
        let zip = manifest::download(&manifest.bepinex.url, &manifest.bepinex.sha256, &|f| {
            progress(
                &format!("Downloading BepInEx {}", manifest.bepinex.version),
                Some(f),
            )
        })?;
        if let Some(old) = record.bepinex.take() {
            remove_files(game, &old.files);
        }
        progress("Installing BepInEx", None);
        let files = extract(game, &zip, &BTreeSet::new(), &mut Vec::new())?;
        record.bepinex = Some(BepInExRecord {
            version: manifest.bepinex.version.clone(),
            files,
        });
    }

    // The mod.
    progress(
        &format!("Downloading ShroomVR {}", manifest.version),
        Some(0.0),
    );
    let zip = manifest::download(&manifest.package.url, &manifest.package.sha256, &|f| {
        progress(
            &format!("Downloading ShroomVR {}", manifest.version),
            Some(f),
        )
    })?;
    progress("Installing ShroomVR", None);
    let ours: BTreeSet<String> = previous
        .iter()
        .flat_map(|r| r.files.iter().cloned())
        .collect();
    let mut backups = std::mem::take(&mut record.backups);
    record.files = extract(game, &zip, &ours, &mut backups)?;
    record.backups = backups;
    if let Some(previous) = &previous {
        let current: BTreeSet<&String> = record.files.iter().collect();
        let stale: Vec<String> = previous
            .files
            .iter()
            .filter(|f| !current.contains(f))
            .cloned()
            .collect();
        remove_files(game, &stale);
    }
    write_record(game, &record)?;

    progress("Adding the launcher", None);
    let proton = steam.proton()?;
    write_launcher(steam, game, &proton)?;
    let art = Art::load(steam);
    write_desktop_entry(&art)?;
    if options.steam_entry {
        progress("Adding Shroom and Gloom VR to your Steam library", None);
        add_shortcut(steam, &art)?;
    }
    progress(
        &format!("ShroomVR {} is installed", manifest.version),
        Some(1.0),
    );
    Ok(())
}

/// Removes the mod, puts back replaced game files and removes BepInEx if the installer added it and
/// nothing else uses it. Steam must not be running when `steam_entry` is set.
pub fn uninstall(
    steam: &Steam,
    game: &Path,
    steam_entry: bool,
    progress: Progress,
) -> Result<(), String> {
    if steam::game_running() {
        return Err("Close Shroom and Gloom first.".into());
    }
    progress("Removing ShroomVR", None);
    let record = read_record(game).unwrap_or_default();
    remove_files(game, &record.files);
    let _ = fs::remove_dir_all(game.join(PLUGIN_DIR));
    restore_backups(game, &record.backups);

    let others_use_bepinex = fs::read_dir(game.join("BepInEx/plugins"))
        .into_iter()
        .flatten()
        .flatten()
        .next()
        .is_some();
    match &record.bepinex {
        Some(bepinex) if !others_use_bepinex => {
            progress("Removing BepInEx", None);
            remove_files(game, &bepinex.files);
            for name in BEPINEX_ROOT_FILES {
                let _ = fs::remove_file(game.join(name));
            }
            let _ = fs::remove_dir_all(game.join("BepInEx"));
            let _ = fs::remove_dir_all(game.join("dotnet"));
        }
        _ => {
            let _ = fs::remove_file(game.join(CONFIG_FILE));
        }
    }
    let _ = fs::remove_dir_all(game.join(RECORD_DIR));

    progress("Removing the launcher", None);
    for path in user_files() {
        let _ = fs::remove_file(path);
    }
    if steam_entry {
        progress("Removing Shroom and Gloom VR from your Steam library", None);
        remove_shortcut(steam)?;
    }
    progress("ShroomVR is uninstalled", Some(1.0));
    Ok(())
}

/// Extracts a zip that mirrors the game folder. Existing files that aren't ours are backed up first.
/// Returns the files written, relative to the game folder.
fn extract(
    game: &Path,
    zip: &[u8],
    ours: &BTreeSet<String>,
    backups: &mut Vec<String>,
) -> Result<Vec<String>, String> {
    let mut archive =
        zip::ZipArchive::new(Cursor::new(zip)).map_err(|e| format!("Broken download: {e}"))?;
    let mut written = Vec::new();
    for i in 0..archive.len() {
        let mut entry = archive
            .by_index(i)
            .map_err(|e| format!("Broken download: {e}"))?;
        if entry.is_dir() {
            continue;
        }
        let relative = entry
            .enclosed_name()
            .filter(|p| p.components().all(|c| matches!(c, Component::Normal(_))))
            .ok_or_else(|| {
                format!(
                    "Refusing to unpack {} outside the game folder",
                    entry.name().unwrap_or_default()
                )
            })?;
        let key = relative.to_string_lossy().replace('\\', "/");
        let dest = game.join(&relative);
        if dest.is_file() && !ours.contains(&key) && !backups.contains(&key) {
            let backup = game.join(RECORD_DIR).join("backup").join(&relative);
            fs::create_dir_all(backup.parent().unwrap()).map_err(|e| e.to_string())?;
            fs::copy(&dest, &backup).map_err(|e| format!("Can't back up {key}: {e}"))?;
            backups.push(key.clone());
        }
        if let Some(parent) = dest.parent() {
            fs::create_dir_all(parent)
                .map_err(|e| format!("Can't create {}: {e}", parent.display()))?;
        }
        let mut bytes = Vec::with_capacity(entry.size() as usize);
        entry
            .read_to_end(&mut bytes)
            .map_err(|e| format!("Broken download: {e}"))?;
        fs::write(&dest, bytes).map_err(|e| format!("Can't write {}: {e}", dest.display()))?;
        written.push(key);
    }
    if written.is_empty() {
        return Err("The download is empty".into());
    }
    Ok(written)
}

fn remove_files(game: &Path, files: &[String]) {
    let mut dirs = BTreeSet::new();
    for file in files {
        let path = game.join(file);
        if path.starts_with(game) && fs::remove_file(&path).is_ok() {
            let mut dir = path.parent();
            while let Some(d) = dir.filter(|d| *d != game) {
                dirs.insert(d.to_path_buf());
                dir = d.parent();
            }
        }
    }
    // deepest first, only the ones that are now empty
    for dir in dirs.iter().rev() {
        let _ = fs::remove_dir(dir);
    }
}

fn restore_backups(game: &Path, backups: &[String]) {
    for file in backups {
        let backup = game.join(RECORD_DIR).join("backup").join(file);
        let dest = game.join(file);
        if let Some(parent) = dest.parent() {
            let _ = fs::create_dir_all(parent);
        }
        let _ = fs::copy(&backup, &dest);
    }
}

// ------------------------------------------------------------- user-level files --

fn data_home() -> PathBuf {
    std::env::var_os("XDG_DATA_HOME")
        .map(PathBuf::from)
        .unwrap_or_else(|| steam::home().join(".local/share"))
}

pub fn launcher_path() -> PathBuf {
    steam::home().join(".local/bin/shroomvr")
}

fn desktop_path() -> PathBuf {
    data_home().join("applications/shroom-and-gloom-vr.desktop")
}

fn icon_path() -> PathBuf {
    data_home().join("icons/hicolor/256x256/apps/shroom-and-gloom-vr.png")
}

fn user_files() -> Vec<PathBuf> {
    vec![launcher_path(), desktop_path(), icon_path()]
}

/// The launcher starts SteamVR when needed and runs the game through Proton with BepInEx enabled.
/// Launching the game normally from Steam stays unmodded: BepInEx only loads with the winhttp override.
fn write_launcher(steam: &Steam, game: &Path, proton: &steam::Proton) -> Result<(), String> {
    let quote = |p: &Path| format!("'{}'", p.to_string_lossy().replace('\'', r"'\''"));
    let run = match &proton.runtime {
        Some(entry) => format!(
            "exec {} --verb=waitforexitandrun -- \\\n    {} waitforexitandrun \\\n    \"$game_dir/{GAME_EXE}\" \"$mode\" \"$@\"",
            quote(entry),
            quote(&proton.script)
        ),
        None => format!(
            "exec {} waitforexitandrun \"$game_dir/{GAME_EXE}\" \"$mode\" \"$@\"",
            quote(&proton.script)
        ),
    };
    let script = format!(
        r#"#!/usr/bin/env bash
# Shroom and Gloom VR launcher, written by the ShroomVR installer. Run the installer again to update it.
# Usage: shroomvr [--flat] [--proton-log] [game arguments...]
set -euo pipefail
unset LD_PRELOAD
steam_root={steam_root}
game_dir={game_dir}
state_dir="${{XDG_STATE_HOME:-$HOME/.local/state}}/shroomvr"
mkdir -p "$state_dir"
exec >>"$state_dir/launch.log" 2>&1
date -Is
mode=-shroomvr-vr
while [ $# -gt 0 ]; do
    case "$1" in
        --flat) mode=-shroomvr-novr; shift ;;
        --proton-log) export PROTON_LOG=1 PROTON_LOG_DIR="$state_dir"; shift ;;
        *) break ;;
    esac
done
pgrep -x steam >/dev/null || {{ echo 'Start Steam first'; exit 1; }}
if pgrep -f '{exe_pattern}' >/dev/null; then
    echo 'Shroom and Gloom is already running'
    exit 0
fi
if [ "$mode" = -shroomvr-vr ] && ! pgrep -x vrcompositor >/dev/null; then
    steam -applaunch {steamvr}
    for _ in $(seq 1 60); do pgrep -x vrcompositor >/dev/null && break; sleep 1; done
    pgrep -x vrcompositor >/dev/null || {{ echo 'SteamVR did not start'; exit 1; }}
fi
export SteamAppId={app} SteamGameId={app}
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$steam_root"
export STEAM_COMPAT_DATA_PATH={compat}
export STEAM_COMPAT_INSTALL_PATH="$game_dir"
export WINEDLLOVERRIDES="winhttp=n,b"
cd "$game_dir"
{run}
"#,
        steam_root = quote(&steam.root),
        game_dir = quote(game),
        exe_pattern = GAME_EXE.replace('.', r"\.").replacen("exe", "ex[e]", 1),
        steamvr = steam::STEAMVR_APP_ID,
        app = APP_ID,
        compat = quote(&steam.compat_data(game)),
    );
    let path = launcher_path();
    fs::create_dir_all(path.parent().unwrap()).map_err(|e| e.to_string())?;
    fs::write(&path, script).map_err(|e| format!("Can't write {}: {e}", path.display()))?;
    fs::set_permissions(&path, fs::Permissions::from_mode(0o755)).map_err(|e| e.to_string())
}

fn write_desktop_entry(art: &Art) -> Result<(), String> {
    let icon = icon_path();
    fs::create_dir_all(icon.parent().unwrap()).map_err(|e| e.to_string())?;
    art.icon()
        .save_with_format(&icon, image::ImageFormat::Png)
        .map_err(|e| e.to_string())?;
    let entry = format!(
        "[Desktop Entry]\nType=Application\nName={SHORTCUT_NAME}\nComment=Play Shroom and Gloom in VR (ShroomVR)\nExec={}\nIcon=shroom-and-gloom-vr\nTerminal=false\nCategories=Game;\n",
        launcher_path().display()
    );
    let path = desktop_path();
    fs::create_dir_all(path.parent().unwrap()).map_err(|e| e.to_string())?;
    fs::write(&path, entry).map_err(|e| format!("Can't write {}: {e}", path.display()))
}

// ---------------------------------------------------------------- Steam library --

/// The id Steam gives a non-Steam shortcut; its grid art is named after it.
pub fn shortcut_id(exe: &str) -> u32 {
    let mut hasher = crc32fast::Hasher::new();
    hasher.update(exe.as_bytes());
    hasher.update(SHORTCUT_NAME.as_bytes());
    hasher.finalize() | 0x8000_0000
}

fn shortcut_exe() -> String {
    format!("\"{}\"", launcher_path().display())
}

fn is_ours(entry: &Bin) -> bool {
    let name = entry
        .get("appname")
        .or_else(|| entry.get("AppName"))
        .and_then(Bin::str);
    let exe = entry
        .get("exe")
        .or_else(|| entry.get("Exe"))
        .and_then(Bin::str);
    name == Some(SHORTCUT_NAME) || exe == Some(&shortcut_exe())
}

fn read_shortcuts(path: &Path) -> Result<Vec<(String, Bin)>, String> {
    match fs::read(path) {
        Ok(bytes) => {
            let mut doc =
                vdf::read_bin(&bytes).map_err(|e| format!("Can't read {}: {e}", path.display()))?;
            if doc
                .iter()
                .all(|(k, _)| !k.eq_ignore_ascii_case("shortcuts"))
            {
                doc.push(("shortcuts".into(), Bin::Map(Vec::new())));
            }
            Ok(doc)
        }
        Err(_) => Ok(vec![("shortcuts".into(), Bin::Map(Vec::new()))]),
    }
}

fn shortcuts_mut(doc: &mut [(String, Bin)]) -> &mut Vec<(String, Bin)> {
    match doc
        .iter_mut()
        .find(|(k, _)| k.eq_ignore_ascii_case("shortcuts"))
    {
        Some((_, Bin::Map(entries))) => entries,
        _ => unreachable!("read_shortcuts always has a shortcuts map"),
    }
}

fn write_shortcuts(path: &Path, doc: &[(String, Bin)]) -> Result<(), String> {
    if path.is_file() {
        let _ = fs::copy(path, path.with_extension("vdf.shroomvr-backup"));
    }
    fs::create_dir_all(path.parent().unwrap()).map_err(|e| e.to_string())?;
    fs::write(path, vdf::write_bin(doc)).map_err(|e| format!("Can't write {}: {e}", path.display()))
}

/// Adds (or refreshes) the "Shroom and Gloom VR" entry for the Steam user who logged in last.
fn add_shortcut(steam: &Steam, art: &Art) -> Result<(), String> {
    let user = steam
        .users()
        .into_iter()
        .next()
        .ok_or("No Steam user found. Log in to Steam once.")?;
    let path = user.join("config/shortcuts.vdf");
    let mut doc = read_shortcuts(&path)?;
    let entries = shortcuts_mut(&mut doc);
    let exe = shortcut_exe();
    let id = shortcut_id(&exe);
    let icon = icon_path();
    let previous = entries
        .iter()
        .find(|(_, e)| is_ours(e))
        .map(|(_, e)| e.clone());
    let keep = |key: &str, default: Bin| {
        previous
            .as_ref()
            .and_then(|p| p.get(key).cloned())
            .unwrap_or(default)
    };
    let entry = Bin::Map(vec![
        ("appid".into(), Bin::Int(id)),
        ("appname".into(), Bin::Str(SHORTCUT_NAME.into())),
        ("exe".into(), Bin::Str(exe)),
        (
            "StartDir".into(),
            Bin::Str(format!("\"{}\"", steam::home().display())),
        ),
        ("icon".into(), Bin::Str(icon.to_string_lossy().into_owned())),
        (
            "ShortcutPath".into(),
            Bin::Str(desktop_path().to_string_lossy().into_owned()),
        ),
        (
            "LaunchOptions".into(),
            keep("LaunchOptions", Bin::Str(String::new())),
        ),
        ("IsHidden".into(), keep("IsHidden", Bin::Int(0))),
        ("AllowDesktopConfig".into(), Bin::Int(1)),
        ("AllowOverlay".into(), Bin::Int(1)),
        ("OpenVR".into(), Bin::Int(1)),
        ("Devkit".into(), Bin::Int(0)),
        ("DevkitGameID".into(), Bin::Str(String::new())),
        ("DevkitOverrideAppID".into(), Bin::Int(0)),
        ("LastPlayTime".into(), keep("LastPlayTime", Bin::Int(0))),
        ("FlatpakAppID".into(), Bin::Str(String::new())),
        ("sortas".into(), Bin::Str(String::new())),
        (
            "tags".into(),
            Bin::Map(vec![("0".into(), Bin::Str("VR".into()))]),
        ),
    ]);
    match entries.iter_mut().find(|(_, e)| is_ours(e)) {
        Some((_, existing)) => *existing = entry,
        None => entries.push((entries.len().to_string(), entry)),
    }
    write_shortcuts(&path, &doc)?;
    art.write_grid(&user.join("config/grid"), id)
}

fn remove_shortcut(steam: &Steam) -> Result<(), String> {
    for user in steam.users() {
        let path = user.join("config/shortcuts.vdf");
        if !path.is_file() {
            continue;
        }
        let mut doc = read_shortcuts(&path)?;
        let entries = shortcuts_mut(&mut doc);
        let before = entries.len();
        let ids: Vec<u32> = entries
            .iter()
            .filter(|(_, e)| is_ours(e))
            .filter_map(|(_, e)| match e.get("appid") {
                Some(Bin::Int(id)) => Some(*id),
                _ => None,
            })
            .collect();
        entries.retain(|(_, e)| !is_ours(e));
        if entries.len() == before {
            continue;
        }
        for (i, (key, _)) in entries.iter_mut().enumerate() {
            *key = i.to_string();
        }
        write_shortcuts(&path, &doc)?;
        for id in ids {
            for name in [
                format!("{id}p.jpg"),
                format!("{id}.jpg"),
                format!("{id}_hero.jpg"),
                format!("{id}_logo.png"),
            ] {
                let _ = fs::remove_file(user.join("config/grid").join(name));
            }
        }
    }
    Ok(())
}

pub fn has_shortcut(steam: &Steam) -> bool {
    steam.users().iter().any(|user| {
        read_shortcuts(&user.join("config/shortcuts.vdf"))
            .map(|mut doc| shortcuts_mut(&mut doc).iter().any(|(_, e)| is_ours(e)))
            .unwrap_or(false)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shortcut_id_matches_steam() {
        // Steam's legacy shortcut id: crc32(exe + name) with the top bit set.
        let mut h = crc32fast::Hasher::new();
        h.update(b"\"/home/user/.local/bin/shroomvr\"Shroom and Gloom VR");
        assert_eq!(h.finalize() | 0x8000_0000, 2499792949);
    }

    #[test]
    fn extract_backs_up_and_records() {
        let dir = std::env::temp_dir().join(format!("shroomvr-test-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(dir.join("a")).unwrap();
        fs::write(dir.join("a/game.dll"), b"original").unwrap();

        let mut zip_bytes = Vec::new();
        {
            let mut w = zip::ZipWriter::new(Cursor::new(&mut zip_bytes));
            let opts = zip::write::SimpleFileOptions::default();
            w.start_file("a/game.dll", opts).unwrap();
            std::io::Write::write_all(&mut w, b"modded").unwrap();
            w.start_file("b/new.json", opts).unwrap();
            std::io::Write::write_all(&mut w, b"{}").unwrap();
            w.finish().unwrap();
        }
        let mut backups = Vec::new();
        let files = extract(&dir, &zip_bytes, &BTreeSet::new(), &mut backups).unwrap();
        assert_eq!(files, vec!["a/game.dll", "b/new.json"]);
        assert_eq!(backups, vec!["a/game.dll"]);
        remove_files(&dir, &files);
        restore_backups(&dir, &backups);
        assert_eq!(fs::read(dir.join("a/game.dll")).unwrap(), b"original");
        assert!(!dir.join("b").exists());
        let _ = fs::remove_dir_all(&dir);
    }
}
