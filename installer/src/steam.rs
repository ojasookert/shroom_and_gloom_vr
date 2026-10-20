//! Finding Steam, its libraries, the game, Proton and the Steam users on this machine.

use crate::vdf::{self, Kv};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, Instant};

pub const APP_ID: u32 = 3271280;
pub const STEAMVR_APP_ID: u32 = 250820;
pub const GAME_EXE: &str = "Shroom and Gloom.exe";
const GAME_FOLDER: &str = "Shroom and Gloom";

pub struct Steam {
    pub root: PathBuf,
    pub libraries: Vec<PathBuf>,
}

impl Steam {
    pub fn find() -> Option<Steam> {
        let home = home();
        let candidates = [
            home.join(".local/share/Steam"),
            home.join(".steam/steam"),
            home.join(".steam/root"),
            home.join(".var/app/com.valvesoftware.Steam/.local/share/Steam"),
        ];
        let root = candidates
            .iter()
            .filter_map(|p| fs::canonicalize(p).ok())
            .find(|p| p.join("steamapps").is_dir())?;
        let mut libraries = vec![root.clone()];
        if let Ok(text) = fs::read_to_string(root.join("steamapps/libraryfolders.vdf")) {
            let doc = vdf::parse(&text);
            if let Some(folders) = doc.get("libraryfolders") {
                for (_, folder) in folders.entries() {
                    if let Some(path) = folder.get("path").and_then(Kv::str) {
                        let path = fs::canonicalize(path).unwrap_or_else(|_| PathBuf::from(path));
                        if path.join("steamapps").is_dir() && !libraries.contains(&path) {
                            libraries.push(path);
                        }
                    }
                }
            }
        }
        Some(Steam { root, libraries })
    }

    pub fn is_flatpak(&self) -> bool {
        self.root
            .to_string_lossy()
            .contains("/.var/app/com.valvesoftware.Steam/")
    }

    /// The library an app is installed in, with its parsed appmanifest.
    pub fn app(&self, app_id: u32) -> Option<(PathBuf, Kv)> {
        self.libraries.iter().find_map(|lib| {
            let text =
                fs::read_to_string(lib.join(format!("steamapps/appmanifest_{app_id}.acf"))).ok()?;
            let doc = vdf::parse(&text);
            let state = doc.get("AppState")?.clone();
            Some((lib.clone(), state))
        })
    }

    /// Where Steam installed the game, if it did.
    pub fn find_game(&self) -> Option<PathBuf> {
        if let Some((lib, state)) = self.app(APP_ID) {
            let folder = state
                .get("installdir")
                .and_then(Kv::str)
                .unwrap_or(GAME_FOLDER);
            let dir = lib.join("steamapps/common").join(folder);
            if is_game_dir(&dir) {
                return Some(dir);
            }
        }
        self.libraries
            .iter()
            .map(|lib| lib.join("steamapps/common").join(GAME_FOLDER))
            .find(|dir| is_game_dir(dir))
    }

    pub fn build_id(&self) -> Option<u64> {
        let (_, state) = self.app(APP_ID)?;
        state.get("buildid")?.str()?.parse().ok()
    }

    /// The game's Proton prefix. Steam keeps it in the library the game is installed in.
    pub fn compat_data(&self, game_dir: &Path) -> PathBuf {
        let library = game_dir
            .parent()
            .and_then(Path::parent)
            .and_then(Path::parent);
        let mut candidates: Vec<PathBuf> = library.into_iter().map(Path::to_path_buf).collect();
        candidates.extend(self.libraries.iter().cloned());
        candidates
            .iter()
            .map(|lib| lib.join(format!("steamapps/compatdata/{APP_ID}")))
            .find(|p| p.join("pfx").is_dir())
            .unwrap_or_else(|| candidates[0].join(format!("steamapps/compatdata/{APP_ID}")))
    }

    pub fn has_steamvr(&self) -> bool {
        self.app(STEAMVR_APP_ID).is_some()
    }

    /// The Proton build Steam runs the game with, and the Steam Linux Runtime it needs.
    pub fn proton(&self) -> Result<Proton, String> {
        let wanted = self.compat_tool_name();
        let dir = wanted
            .as_deref()
            .and_then(|name| self.compat_tool_dir(name))
            .or_else(|| self.fallback_proton())
            .ok_or_else(|| "No Proton found. Install Proton Experimental in Steam (Library → search \"Proton\").".to_string())?;
        let manifest = fs::read_to_string(dir.join("toolmanifest.vdf"))
            .map(|t| vdf::parse(&t))
            .ok();
        let runtime = match manifest
            .as_ref()
            .and_then(|m| m.path(&["manifest", "require_tool_appid"]))
            .and_then(Kv::str)
            .and_then(|id| id.parse::<u32>().ok())
        {
            Some(id) => {
                let (lib, state) = self.app(id).ok_or_else(|| {
                    format!("{} needs a Steam Linux Runtime (app {id}) that isn't installed. Start any game with it once.", dir_name(&dir))
                })?;
                let folder = state
                    .get("installdir")
                    .and_then(Kv::str)
                    .unwrap_or_default()
                    .to_string();
                let entry = lib
                    .join("steamapps/common")
                    .join(folder)
                    .join("_v2-entry-point");
                entry.is_file().then_some(entry)
            }
            None => None,
        };
        Ok(Proton {
            script: dir.join("proton"),
            runtime,
        })
    }

    /// The compat tool chosen for the game in Steam, or the default one.
    fn compat_tool_name(&self) -> Option<String> {
        let text = fs::read_to_string(self.root.join("config/config.vdf")).ok()?;
        let doc = vdf::parse(&text);
        let mapping = doc.path(&[
            "InstallConfigStore",
            "Software",
            "Valve",
            "Steam",
            "CompatToolMapping",
        ])?;
        [APP_ID.to_string(), "0".to_string()]
            .iter()
            .filter_map(|id| mapping.get(id)?.get("name")?.str())
            .find(|name| !name.is_empty())
            .map(str::to_string)
    }

    fn compat_tool_dir(&self, name: &str) -> Option<PathBuf> {
        // tools you added yourself (GE-Proton and friends)
        for base in [
            self.root.join("compatibilitytools.d"),
            PathBuf::from("/usr/share/steam/compatibilitytools.d"),
        ] {
            for entry in fs::read_dir(&base).into_iter().flatten().flatten() {
                let Ok(text) = fs::read_to_string(entry.path().join("compatibilitytool.vdf"))
                else {
                    continue;
                };
                let doc = vdf::parse(&text);
                let Some(tool) = doc.path(&["compatibilitytools", "compat_tools", name]) else {
                    continue;
                };
                let install = tool.get("install_path").and_then(Kv::str).unwrap_or(".");
                let dir = entry.path().join(install);
                if dir.join("proton").is_file() {
                    return Some(dir);
                }
            }
        }
        // Valve's: proton_experimental, proton_hotfix, proton_9, proton_10, proton_63...
        let folders: Vec<String> = match name {
            "proton_experimental" => vec!["Proton - Experimental".into()],
            "proton_hotfix" => vec!["Proton Hotfix".into()],
            n => {
                let digits = n.strip_prefix("proton_")?;
                if !digits.chars().all(|c| c.is_ascii_digit()) {
                    return None;
                }
                let dotted =
                    if digits.len() >= 2 && digits.len() < 3 && digits.as_bytes()[0] != b'1' {
                        format!("{}.{}", &digits[..1], &digits[1..])
                    } else {
                        format!("{digits}.0")
                    };
                vec![format!("Proton {dotted}"), format!("Proton {digits}")]
            }
        };
        self.libraries
            .iter()
            .flat_map(|lib| {
                folders
                    .iter()
                    .map(move |f| lib.join("steamapps/common").join(f))
            })
            .find(|dir| dir.join("proton").is_file())
    }

    fn fallback_proton(&self) -> Option<PathBuf> {
        let mut found: Vec<PathBuf> = self
            .libraries
            .iter()
            .flat_map(|lib| {
                fs::read_dir(lib.join("steamapps/common"))
                    .into_iter()
                    .flatten()
                    .flatten()
            })
            .map(|e| e.path())
            .filter(|p| dir_name(p).starts_with("Proton") && p.join("proton").is_file())
            .collect();
        found.sort_by_key(|p| {
            let name = dir_name(p);
            (
                name.contains("Experimental"),
                name.split_whitespace()
                    .nth(1)
                    .and_then(|v| v.split('.').next()?.parse::<u32>().ok())
                    .unwrap_or(0),
            )
        });
        found.pop()
    }

    /// Steam users on this machine, the one that logged in last first.
    pub fn users(&self) -> Vec<PathBuf> {
        let userdata = self.root.join("userdata");
        let mut users: Vec<PathBuf> = fs::read_dir(&userdata)
            .into_iter()
            .flatten()
            .flatten()
            .map(|e| e.path())
            .filter(|p| {
                p.join("config").is_dir()
                    && dir_name(p).chars().all(|c| c.is_ascii_digit())
                    && dir_name(p) != "0"
            })
            .collect();
        if let Some(recent) = self.most_recent_account() {
            users.sort_by_key(|p| dir_name(p) != recent);
        }
        users
    }

    fn most_recent_account(&self) -> Option<String> {
        let text = fs::read_to_string(self.root.join("config/loginusers.vdf")).ok()?;
        let doc = vdf::parse(&text);
        let (id, _) = doc
            .get("users")?
            .entries()
            .iter()
            .find(|(_, u)| u.get("MostRecent").and_then(Kv::str) == Some("1"))?;
        let steam_id: u64 = id.parse().ok()?;
        Some((steam_id & 0xFFFF_FFFF).to_string())
    }

    /// Steam's cached library art for an app, newest first per file name.
    pub fn cached_art(&self, app_id: u32, file: &str) -> Option<(PathBuf, Option<String>)> {
        let base = self
            .root
            .join("appcache/librarycache")
            .join(app_id.to_string());
        if base.join(file).is_file() {
            return Some((base.join(file), None));
        }
        fs::read_dir(&base).ok()?.flatten().find_map(|e| {
            let p = e.path().join(file);
            p.is_file()
                .then(|| (p, Some(e.file_name().to_string_lossy().into_owned())))
        })
    }
}

pub struct Proton {
    pub script: PathBuf,
    /// Steam Linux Runtime entry point the Proton build runs inside, if it needs one.
    pub runtime: Option<PathBuf>,
}

pub fn is_game_dir(dir: &Path) -> bool {
    dir.join(GAME_EXE).is_file() && dir.join("GameAssembly.dll").is_file()
}

pub fn home() -> PathBuf {
    std::env::var_os("HOME")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("/"))
}

fn dir_name(p: &Path) -> String {
    p.file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_default()
}

// ------------------------------------------------------------------ processes --

/// Process ids whose command name or command line matches.
fn processes(matches: impl Fn(&str, &str) -> bool) -> Vec<u32> {
    let mut pids = Vec::new();
    for entry in fs::read_dir("/proc").into_iter().flatten().flatten() {
        let Some(pid) = entry
            .file_name()
            .to_str()
            .and_then(|s| s.parse::<u32>().ok())
        else {
            continue;
        };
        let comm = fs::read_to_string(entry.path().join("comm")).unwrap_or_default();
        let cmdline = fs::read(entry.path().join("cmdline")).unwrap_or_default();
        let cmdline = String::from_utf8_lossy(&cmdline).replace('\0', " ");
        if matches(comm.trim(), &cmdline) {
            pids.push(pid);
        }
    }
    pids
}

pub fn steam_running() -> bool {
    !processes(|comm, _| comm == "steam").is_empty()
}

pub fn game_running() -> bool {
    !processes(|_, cmd| cmd.contains(GAME_EXE) && !cmd.contains("shroomvr-installer")).is_empty()
}

/// The arguments the running Steam client was started with, to start it the same way again.
fn steam_args() -> Vec<String> {
    let Some(pid) = processes(|comm, _| comm == "steam").into_iter().min() else {
        return Vec::new();
    };
    let cmdline = fs::read(format!("/proc/{pid}/cmdline")).unwrap_or_default();
    cmdline
        .split(|&b| b == 0)
        .skip(1)
        .map(|a| String::from_utf8_lossy(a).into_owned())
        .filter(|a| !a.is_empty() && !a.starts_with("-srt-") && !a.starts_with("steam://"))
        .collect()
}

/// Asks Steam to exit and waits for it. Returns the arguments to start it with again.
pub fn shutdown_steam(steam: &Steam) -> Result<Vec<String>, String> {
    let args = steam_args();
    let status = steam_command(steam).arg("-shutdown").spawn();
    if let Err(e) = status {
        return Err(format!("Couldn't ask Steam to close: {e}"));
    }
    let start = Instant::now();
    let mut ui_closed_at = None;
    while steam_running() {
        // The Linux client sometimes hangs after its windows have closed and it has saved
        // everything. Once the UI has been gone for a while, end it.
        let ui_running = !processes(|comm, _| comm == "steamwebhelper").is_empty();
        let since = *ui_closed_at.get_or_insert_with(Instant::now);
        if ui_running {
            ui_closed_at = None;
        } else if since.elapsed() > Duration::from_secs(20) {
            for pid in processes(|comm, _| comm == "steam") {
                signal(pid, 15);
            }
            std::thread::sleep(Duration::from_secs(5));
            for pid in processes(|comm, _| comm == "steam") {
                signal(pid, 9);
            }
        }
        if start.elapsed() > Duration::from_secs(120) {
            return Err("Steam didn't close. Close it yourself and try again.".into());
        }
        std::thread::sleep(Duration::from_millis(500));
    }
    std::thread::sleep(Duration::from_secs(1));
    Ok(args)
}

fn signal(pid: u32, sig: i32) {
    unsafe extern "C" {
        fn kill(pid: i32, sig: i32) -> i32;
    }
    unsafe {
        kill(pid as i32, sig);
    }
}

pub fn start_steam(steam: &Steam, args: &[String]) {
    let _ = detached(steam_command(steam).args(args));
}

fn steam_command(steam: &Steam) -> Command {
    if steam.is_flatpak() {
        let mut c = Command::new("flatpak");
        c.args(["run", "com.valvesoftware.Steam"]);
        c
    } else {
        Command::new("steam")
    }
}

/// Starts a program in its own session, so it outlives the installer.
pub fn detached(command: &mut Command) -> std::io::Result<()> {
    use std::os::unix::process::CommandExt;
    use std::process::Stdio;
    command
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    unsafe {
        command.pre_exec(|| {
            libc_setsid();
            Ok(())
        });
    }
    command.spawn().map(|_| ())
}

fn libc_setsid() {
    unsafe extern "C" {
        fn setsid() -> i32;
    }
    unsafe {
        setsid();
    }
}
