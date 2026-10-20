mod art;
mod gui;
mod install;
mod manifest;
mod steam;
mod vdf;

use std::path::PathBuf;
use std::process::ExitCode;

pub const VERSION: &str = match option_env!("SHROOMVR_VERSION") {
    Some(v) => v,
    None => "0.0.0-dev",
};

const USAGE: &str = "\
ShroomVR installer

Without arguments it opens the installer window. Commands for the terminal:
  status                 show what is installed
  install [--yes]        install or update to the latest release
  uninstall [--yes]      remove ShroomVR (and BepInEx if the installer added it)
  launch [--flat]        start the game in VR (or on the monitor)

Options:
  --game-dir DIR         use this game folder instead of the one Steam knows about
  --no-steam-entry       don't add Shroom and Gloom VR to the Steam library
  --yes                  don't ask: install on untested game versions, close Steam when needed";

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.is_empty() {
        return match gui::run() {
            Ok(()) => ExitCode::SUCCESS,
            Err(e) => {
                eprintln!("Couldn't open the installer window: {e}\n\n{USAGE}");
                ExitCode::FAILURE
            }
        };
    }
    match cli(&args) {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("error: {e}");
            ExitCode::FAILURE
        }
    }
}

fn cli(args: &[String]) -> Result<(), String> {
    let mut command = None;
    let mut game_dir = None;
    let mut yes = false;
    let mut steam_entry = true;
    let mut flat = false;
    let mut it = args.iter();
    while let Some(arg) = it.next() {
        match arg.as_str() {
            "--game-dir" => {
                game_dir = Some(PathBuf::from(it.next().ok_or("--game-dir needs a folder")?))
            }
            "--yes" | "-y" => yes = true,
            "--no-steam-entry" => steam_entry = false,
            "--flat" => flat = true,
            "--version" => {
                println!("{VERSION}");
                return Ok(());
            }
            "-h" | "--help" | "help" => {
                println!("{USAGE}");
                return Ok(());
            }
            c if command.is_none() && !c.starts_with('-') => command = Some(c.to_string()),
            other => return Err(format!("unknown argument {other}\n\n{USAGE}")),
        }
    }

    if command.as_deref() == Some("launch") {
        return launch(flat);
    }
    let steam = steam::Steam::find().ok_or("Steam isn't installed for this user.")?;
    if steam.is_flatpak() {
        return Err("Flatpak Steam isn't supported yet.".into());
    }
    let game = match game_dir {
        Some(dir) if steam::is_game_dir(&dir) => dir,
        Some(dir) => {
            return Err(format!(
                "{} doesn't contain {}",
                dir.display(),
                steam::GAME_EXE
            ));
        }
        None => steam
            .find_game()
            .ok_or("Shroom and Gloom isn't installed in any Steam library. Use --game-dir.")?,
    };
    let last = std::cell::RefCell::new(String::new());
    let progress = |line: &str, _: Option<f32>| {
        if *last.borrow() != line {
            println!("{line}");
            *last.borrow_mut() = line.to_string();
        }
    };

    match command.as_deref() {
        Some("status") => {
            println!("Game:      {}", game.display());
            let hash =
                manifest::file_sha256(&game.join("GameAssembly.dll")).map_err(|e| e.to_string())?;
            println!(
                "Build:     {} ({})",
                steam
                    .build_id()
                    .map(|b| b.to_string())
                    .unwrap_or("?".into()),
                &hash[..12]
            );
            match install::read_record(&game) {
                Some(r) => println!("ShroomVR:  {}", r.version),
                None => println!("ShroomVR:  not installed"),
            }
            println!(
                "BepInEx:   {}",
                if install::bepinex_present(&game) {
                    "installed"
                } else {
                    "not installed"
                }
            );
            println!(
                "Library:   {}",
                if install::has_shortcut(&steam) {
                    "Shroom and Gloom VR entry present"
                } else {
                    "no entry"
                }
            );
            match manifest::fetch() {
                Ok(m) => {
                    println!("Latest:    {}", m.version);
                    match m.tested(&hash) {
                        Some(g) => println!("Tested:    yes, game version {}", g.version),
                        None => println!(
                            "Tested:    no, this game build isn't on the list for {}",
                            m.version
                        ),
                    }
                }
                Err(e) => println!("Latest:    {e}"),
            }
            Ok(())
        }
        Some("install") | Some("update") => {
            let m = manifest::fetch()?;
            let hash =
                manifest::file_sha256(&game.join("GameAssembly.dll")).map_err(|e| e.to_string())?;
            if m.tested(&hash).is_none() && !yes {
                return Err(format!(
                    "This game build hasn't been tested with ShroomVR {} (tested: {}). Run with --yes to install anyway.",
                    m.version,
                    tested_list(&m)
                ));
            }
            // Updates leave an existing library entry alone, so they never need Steam closed.
            let steam_entry = steam_entry && !install::has_shortcut(&steam);
            with_steam_closed(&steam, steam_entry, yes, || {
                install::install(
                    &steam,
                    &game,
                    &m,
                    &install::Options { steam_entry },
                    &progress,
                )
            })
        }
        Some("uninstall") => {
            let steam_entry = steam_entry && install::has_shortcut(&steam);
            with_steam_closed(&steam, steam_entry, yes, || {
                install::uninstall(&steam, &game, steam_entry, &progress)
            })
        }
        _ => Err(USAGE.to_string()),
    }
}

/// Steam rewrites its shortcuts when it exits, so library changes happen while it's closed.
fn with_steam_closed(
    steam: &steam::Steam,
    needed: bool,
    yes: bool,
    f: impl FnOnce() -> Result<(), String>,
) -> Result<(), String> {
    if !needed || !steam::steam_running() {
        return f();
    }
    if !yes {
        return Err("Steam has to close briefly to change its library. Run with --yes, or with --no-steam-entry.".into());
    }
    println!("Closing Steam");
    let args = steam::shutdown_steam(steam)?;
    let result = f();
    println!("Starting Steam");
    steam::start_steam(steam, &args);
    result
}

pub fn tested_list(m: &manifest::Manifest) -> String {
    m.game_versions
        .iter()
        .map(|g| match g.steam_build {
            Some(b) => format!("{} (build {b})", g.version),
            None => g.version.clone(),
        })
        .collect::<Vec<_>>()
        .join(", ")
}

pub fn launch(flat: bool) -> Result<(), String> {
    let launcher = install::launcher_path();
    if !launcher.is_file() {
        return Err("ShroomVR isn't installed yet.".into());
    }
    let mut command = std::process::Command::new(&launcher);
    if flat {
        command.arg("--flat");
    }
    steam::detached(&mut command).map_err(|e| format!("Couldn't start {}: {e}", launcher.display()))
}
