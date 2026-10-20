//! The installer window.

use crate::art::{self, Art};
use crate::install::{self, Options};
use crate::manifest::{self, Manifest, REPO};
use crate::steam::{self, Steam};
use eframe::egui::{self, Color32, CornerRadius, Margin, RichText, Stroke, Vec2};
use std::path::{Path, PathBuf};
use std::sync::mpsc::{Receiver, channel};
use std::sync::{Arc, Mutex};

const WIDTH: f32 = 720.0;
const BANNER_HEIGHT: f32 = 200.0;
const BG: Color32 = Color32::from_rgb(22, 13, 33);
const CARD: Color32 = Color32::from_rgb(34, 21, 50);
const TEXT: Color32 = Color32::from_rgb(232, 224, 240);
const DIM: Color32 = Color32::from_rgb(160, 148, 176);
const GREEN: Color32 = Color32::from_rgb(150, 244, 58);
const AMBER: Color32 = Color32::from_rgb(255, 196, 84);
const RED: Color32 = Color32::from_rgb(255, 110, 110);

pub fn run() -> eframe::Result {
    let icon = image::load_from_memory(art::ICON)
        .expect("bundled icon")
        .to_rgba8();
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_title("Shroom and Gloom VR")
            .with_app_id("shroomvr-installer")
            .with_inner_size([WIDTH, 640.0])
            .with_min_inner_size([WIDTH, 560.0])
            .with_icon(egui::IconData {
                width: icon.width(),
                height: icon.height(),
                rgba: icon.into_raw(),
            }),
        ..Default::default()
    };
    eframe::run_native(
        "shroomvr-installer",
        options,
        Box::new(|cc| Ok(Box::new(App::new(&cc.egui_ctx)))),
    )
}

/// What the installer knows about this machine, refreshed after every task.
#[derive(Default)]
struct Status {
    game: Option<PathBuf>,
    game_hash: Option<String>,
    build_id: Option<u64>,
    prefix_ready: bool,
    installed: Option<String>,
    bepinex: bool,
    shortcut: bool,
    steamvr: bool,
}

#[derive(Default)]
struct Task {
    line: String,
    fraction: Option<f32>,
    result: Option<Result<String, String>>,
}

/// What a dialog button does once the dialog is closed.
type Action = Box<dyn FnOnce(&mut App, &egui::Context)>;

enum Dialog {
    Untested {
        build: String,
    },
    CloseSteam {
        uninstall: bool,
    },
    ConfirmUninstall,
    Message {
        title: String,
        text: String,
        error: bool,
    },
}

struct App {
    steam: Option<Arc<Steam>>,
    status: Status,
    custom_game: Option<PathBuf>,
    manifest: Option<Result<Manifest, String>>,
    manifest_rx: Option<Receiver<Result<Manifest, String>>>,
    hash_rx: Option<Receiver<Option<String>>>,
    banner: Option<egui::TextureHandle>,
    banner_rx: Option<Receiver<Option<egui::ColorImage>>>,
    folder_rx: Option<Receiver<Option<PathBuf>>>,
    task: Option<Arc<Mutex<Task>>>,
    dialog: Option<Dialog>,
    steam_entry: bool,
}

impl App {
    fn new(ctx: &egui::Context) -> App {
        style(ctx);
        let steam = Steam::find().map(Arc::new);
        let mut app = App {
            steam,
            status: Status::default(),
            custom_game: None,
            manifest: None,
            manifest_rx: None,
            hash_rx: None,
            banner: None,
            banner_rx: None,
            folder_rx: None,
            task: None,
            dialog: None,
            steam_entry: true,
        };
        app.refresh(ctx);
        app.fetch_manifest(ctx);
        if let Some(steam) = app.steam.clone() {
            let (tx, rx) = channel();
            let ctx = ctx.clone();
            std::thread::spawn(move || {
                let banner = Art::load(&steam)
                    .banner(WIDTH as u32 * 2, BANNER_HEIGHT as u32 * 2)
                    .map(|img| {
                        egui::ColorImage::from_rgba_unmultiplied(
                            [img.width() as usize, img.height() as usize],
                            img.as_raw(),
                        )
                    });
                let _ = tx.send(banner);
                ctx.request_repaint();
            });
            app.banner_rx = Some(rx);
        }
        app
    }

    fn fetch_manifest(&mut self, ctx: &egui::Context) {
        let (tx, rx) = channel();
        let ctx = ctx.clone();
        std::thread::spawn(move || {
            let _ = tx.send(manifest::fetch());
            ctx.request_repaint();
        });
        self.manifest = None;
        self.manifest_rx = Some(rx);
    }

    fn refresh(&mut self, ctx: &egui::Context) {
        let Some(steam) = &self.steam else { return };
        let game = self.custom_game.clone().or_else(|| steam.find_game());
        self.status = Status {
            prefix_ready: game
                .as_ref()
                .is_some_and(|g| steam.compat_data(g).join("pfx").is_dir()),
            installed: game
                .as_ref()
                .and_then(|g| install::read_record(g))
                .map(|r| r.version),
            bepinex: game.as_ref().is_some_and(|g| install::bepinex_present(g)),
            shortcut: install::has_shortcut(steam),
            steamvr: steam.has_steamvr(),
            build_id: steam.build_id(),
            game_hash: None,
            game,
        };
        if let Some(game) = self.status.game.clone() {
            let (tx, rx) = channel();
            let ctx = ctx.clone();
            std::thread::spawn(move || {
                let _ = tx.send(manifest::file_sha256(&game.join("GameAssembly.dll")).ok());
                ctx.request_repaint();
            });
            self.hash_rx = Some(rx);
        }
    }

    fn poll(&mut self, ctx: &egui::Context) {
        if let Some(rx) = &self.manifest_rx
            && let Ok(m) = rx.try_recv()
        {
            self.manifest = Some(m);
            self.manifest_rx = None;
        }
        if let Some(rx) = &self.hash_rx
            && let Ok(h) = rx.try_recv()
        {
            self.status.game_hash = h;
            self.hash_rx = None;
        }
        if let Some(rx) = &self.banner_rx
            && let Ok(img) = rx.try_recv()
        {
            self.banner =
                img.map(|img| ctx.load_texture("banner", img, egui::TextureOptions::LINEAR));
            self.banner_rx = None;
        }
        if let Some(rx) = &self.folder_rx
            && let Ok(folder) = rx.try_recv()
        {
            self.folder_rx = None;
            if let Some(folder) = folder {
                if steam::is_game_dir(&folder) {
                    self.custom_game = Some(folder);
                    self.refresh(ctx);
                } else {
                    self.dialog = Some(Dialog::Message {
                        title: "That's not the game folder".into(),
                        text: format!("{} doesn't contain {}.", folder.display(), steam::GAME_EXE),
                        error: true,
                    });
                }
            }
        }
        let finished = self
            .task
            .as_ref()
            .and_then(|t| t.lock().unwrap().result.clone());
        if let Some(result) = finished {
            self.task = None;
            self.refresh(ctx);
            self.dialog = Some(match result {
                Ok(text) => Dialog::Message {
                    title: "Done".into(),
                    text,
                    error: false,
                },
                Err(text) => Dialog::Message {
                    title: "Something went wrong".into(),
                    text,
                    error: true,
                },
            });
        }
    }

    fn busy(&self) -> bool {
        self.task.is_some()
    }

    /// Install or update, after the checks that need the user to confirm something.
    fn start_install(
        &mut self,
        ctx: &egui::Context,
        confirmed_untested: bool,
        confirmed_steam: bool,
    ) {
        let (Some(steam), Some(game), Some(Ok(manifest))) = (
            self.steam.clone(),
            self.status.game.clone(),
            self.manifest.clone(),
        ) else {
            return;
        };
        if steam::game_running() {
            self.dialog = Some(message(
                "Close the game first",
                "Shroom and Gloom is running. Quit it, then try again.",
            ));
            return;
        }
        let tested = self
            .status
            .game_hash
            .as_deref()
            .and_then(|h| manifest.tested(h))
            .is_some();
        if !tested && !confirmed_untested {
            let build = self
                .status
                .build_id
                .map(|b| format!("Steam build {b}"))
                .unwrap_or_else(|| "This game version".into());
            self.dialog = Some(Dialog::Untested { build });
            return;
        }
        // Updates leave an existing library entry alone, so they never need Steam closed.
        let add_entry = self.steam_entry && !self.status.shortcut;
        let close_steam = add_entry && steam::steam_running();
        if close_steam && !confirmed_steam {
            self.dialog = Some(Dialog::CloseSteam { uninstall: false });
            return;
        }
        let options = Options {
            steam_entry: add_entry,
        };
        let version = manifest.version.clone();
        self.spawn(ctx, close_steam, move |progress| {
            install::install(&steam, &game, &manifest, &options, progress)?;
            Ok(format!("ShroomVR {version} is installed. Start SteamVR and press Launch in VR, or use Shroom and Gloom VR in your Steam library."))
        });
    }

    fn start_uninstall(&mut self, ctx: &egui::Context, confirmed_steam: bool) {
        let (Some(steam), Some(game)) = (self.steam.clone(), self.status.game.clone()) else {
            return;
        };
        if steam::game_running() {
            self.dialog = Some(message(
                "Close the game first",
                "Shroom and Gloom is running. Quit it, then try again.",
            ));
            return;
        }
        let steam_entry = self.status.shortcut;
        let close_steam = steam_entry && steam::steam_running();
        if close_steam && !confirmed_steam {
            self.dialog = Some(Dialog::CloseSteam { uninstall: true });
            return;
        }
        self.spawn(ctx, close_steam, move |progress| {
            install::uninstall(&steam, &game, steam_entry, progress)?;
            Ok("ShroomVR is uninstalled. The game is back to how it was.".into())
        });
    }

    /// Runs a task on a worker thread, closing Steam around it when asked to.
    fn spawn(
        &mut self,
        ctx: &egui::Context,
        close_steam: bool,
        work: impl FnOnce(install::Progress) -> Result<String, String> + Send + 'static,
    ) {
        let Some(steam) = self.steam.clone() else {
            return;
        };
        let task = Arc::new(Mutex::new(Task::default()));
        self.task = Some(task.clone());
        let ctx = ctx.clone();
        std::thread::spawn(move || {
            let report = |line: &str, fraction: Option<f32>| {
                let mut t = task.lock().unwrap();
                t.line = line.to_string();
                t.fraction = fraction;
                ctx.request_repaint();
            };
            let mut restart = None;
            let result = (|| {
                if close_steam {
                    report("Closing Steam", None);
                    restart = Some(steam::shutdown_steam(&steam)?);
                }
                work(&report)
            })();
            if let Some(args) = restart {
                report("Starting Steam", None);
                steam::start_steam(&steam, &args);
            }
            task.lock().unwrap().result = Some(result);
            ctx.request_repaint();
        });
    }

    fn choose_folder(&mut self) {
        let (tx, rx) = channel();
        std::thread::spawn(move || {
            let attempts: [&[&str]; 2] = [
                &[
                    "zenity",
                    "--file-selection",
                    "--directory",
                    "--title=Choose the Shroom and Gloom folder",
                ],
                &[
                    "kdialog",
                    "--getexistingdirectory",
                    "--title",
                    "Choose the Shroom and Gloom folder",
                ],
            ];
            let folder = attempts.iter().find_map(|cmd| {
                let out = std::process::Command::new(cmd[0])
                    .args(&cmd[1..])
                    .output()
                    .ok()?;
                let path = String::from_utf8_lossy(&out.stdout).trim().to_string();
                (out.status.success() && !path.is_empty()).then(|| PathBuf::from(path))
            });
            let _ = tx.send(folder);
        });
        self.folder_rx = Some(rx);
    }
}

impl eframe::App for App {
    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        let ctx = ui.ctx().clone();
        self.poll(&ctx);

        egui::Panel::top("banner")
            .frame(egui::Frame::NONE.fill(BG))
            .show(ui, |ui| self.banner_ui(ui));
        egui::Panel::bottom("footer")
            .frame(
                egui::Frame::NONE
                    .fill(BG)
                    .inner_margin(Margin::symmetric(20, 10)),
            )
            .show(ui, footer);
        egui::CentralPanel::no_frame()
            .frame(
                egui::Frame::NONE
                    .fill(BG)
                    .inner_margin(Margin::symmetric(20, 14)),
            )
            .show(ui, |ui| {
                egui::ScrollArea::vertical().show(ui, |ui| {
                    self.game_card(ui);
                    ui.add_space(10.0);
                    self.mod_card(ui, &ctx);
                });
            });

        self.dialogs(&ctx);
    }
}

impl App {
    fn banner_ui(&self, ui: &mut egui::Ui) {
        let (rect, _) = ui.allocate_exact_size(
            Vec2::new(ui.available_width(), BANNER_HEIGHT),
            egui::Sense::hover(),
        );
        match &self.banner {
            Some(tex) => {
                let uv = egui::Rect::from_min_max(egui::pos2(0.0, 0.0), egui::pos2(1.0, 1.0));
                ui.painter().image(tex.id(), rect, uv, Color32::WHITE);
            }
            None => {
                ui.painter()
                    .rect_filled(rect, 0.0, Color32::from_rgb(40, 22, 64));
                ui.painter().text(
                    rect.left_center() + Vec2::new(28.0, 0.0),
                    egui::Align2::LEFT_CENTER,
                    "Shroom and Gloom VR",
                    egui::FontId::proportional(34.0),
                    GREEN,
                );
            }
        }
    }

    fn game_card(&mut self, ui: &mut egui::Ui) {
        card(ui, "Game", |ui| {
            let Some(steam) = &self.steam else {
                status_line(ui, RED, "Steam isn't installed for this user.");
                return;
            };
            if steam.is_flatpak() {
                status_line(ui, RED, "Flatpak Steam isn't supported yet.");
            }
            match self.status.game.clone() {
                Some(game) => {
                    ui.horizontal(|ui| {
                        ui.label(
                            RichText::new(game.display().to_string())
                                .monospace()
                                .color(TEXT),
                        );
                    });
                    ui.horizontal(|ui| {
                        if ui
                            .add_enabled(
                                !self.busy() && self.folder_rx.is_none(),
                                egui::Button::new("Change…"),
                            )
                            .clicked()
                        {
                            self.choose_folder();
                        }
                        if ui.button("Open folder").clicked() {
                            open(&game);
                        }
                    });
                    ui.add_space(4.0);
                    self.version_line(ui);
                    if self.status.prefix_ready {
                        status_line(ui, GREEN, "Proton prefix is set up.");
                    } else {
                        status_line(
                            ui,
                            AMBER,
                            "Start Shroom and Gloom from Steam once (and quit it) before launching in VR.",
                        );
                    }
                    if !self.status.steamvr {
                        status_line(ui, AMBER, "SteamVR isn't installed. Install it from Steam.");
                    }
                }
                None => {
                    status_line(
                        ui,
                        RED,
                        "Shroom and Gloom isn't installed in any of your Steam libraries.",
                    );
                    if ui
                        .add_enabled(
                            self.folder_rx.is_none(),
                            egui::Button::new("Choose the game folder…"),
                        )
                        .clicked()
                    {
                        self.choose_folder();
                    }
                }
            }
        });
    }

    fn version_line(&self, ui: &mut egui::Ui) {
        let build = self
            .status
            .build_id
            .map(|b| format!("Steam build {b}"))
            .unwrap_or_else(|| "Game".into());
        match (&self.manifest, &self.status.game_hash) {
            (Some(Ok(m)), Some(hash)) => match m.tested(hash) {
                Some(g) => status_line(
                    ui,
                    GREEN,
                    &format!(
                        "Version {} ({build}), tested with ShroomVR {}.",
                        g.version, m.version
                    ),
                ),
                None => status_line(
                    ui,
                    AMBER,
                    &format!(
                        "{build} hasn't been tested with ShroomVR {}. Tested: {}.",
                        m.version,
                        crate::tested_list(m)
                    ),
                ),
            },
            _ => status_line(ui, DIM, &format!("{build}, checking compatibility…")),
        }
    }

    fn mod_card(&mut self, ui: &mut egui::Ui, ctx: &egui::Context) {
        card(ui, "ShroomVR", |ui| {
            let latest = match &self.manifest {
                Some(Ok(m)) => Some(m.version.clone()),
                Some(Err(e)) => {
                    status_line(ui, RED, e);
                    if ui.button("Try again").clicked() {
                        self.fetch_manifest(ctx);
                    }
                    None
                }
                None => {
                    ui.horizontal(|ui| {
                        ui.add(egui::Spinner::new().color(DIM));
                        ui.label(RichText::new("Checking for the latest release…").color(DIM));
                    });
                    None
                }
            };
            match (&self.status.installed, &latest) {
                (Some(installed), Some(latest)) if manifest::newer(latest, installed) => {
                    status_line(
                        ui,
                        AMBER,
                        &format!("Version {installed} is installed. {latest} is available."),
                    )
                }
                (Some(installed), _) => {
                    status_line(ui, GREEN, &format!("Version {installed} is installed."))
                }
                (None, Some(latest)) => status_line(
                    ui,
                    DIM,
                    &format!("Not installed. Latest release: {latest}."),
                ),
                (None, None) => status_line(ui, DIM, "Not installed."),
            }
            if self.status.installed.is_none() && self.status.bepinex {
                status_line(ui, DIM, "BepInEx is already installed and will be kept.");
            }
            if self.status.shortcut {
                status_line(ui, DIM, "Shroom and Gloom VR is in your Steam library.");
            } else {
                ui.add_space(6.0);
                ui.add_enabled(
                    !self.busy(),
                    egui::Checkbox::new(
                        &mut self.steam_entry,
                        "Add Shroom and Gloom VR to my Steam library",
                    ),
                );
            }
            ui.add_space(8.0);

            // The compatibility check needs the game's hash, which takes a moment.
            let ready = self.status.game.is_some()
                && self.status.game_hash.is_some()
                && matches!(self.manifest, Some(Ok(_)))
                && !self.busy();
            let primary = match (&self.status.installed, &latest) {
                (Some(installed), Some(latest)) if manifest::newer(latest, installed) => {
                    format!("Update to {latest}")
                }
                (Some(_), _) => "Reinstall".to_string(),
                (None, _) => "Install".to_string(),
            };
            ui.horizontal(|ui| {
                let install = egui::Button::new(RichText::new(&primary).strong().color(Color32::BLACK).size(16.0))
                    .fill(GREEN)
                    .min_size(Vec2::new(150.0, 36.0));
                if ui.add_enabled(ready, install).clicked() {
                    self.start_install(ctx, false, false);
                }
                let installed = self.status.installed.is_some() && !self.busy();
                let launch = egui::Button::new(RichText::new("Launch in VR").size(16.0)).min_size(Vec2::new(140.0, 36.0));
                if ui.add_enabled(installed && self.status.prefix_ready, launch).clicked() {
                    self.dialog = Some(match crate::launch(false) {
                        Ok(()) => Dialog::Message {
                            title: "Starting".into(),
                            text: "SteamVR starts first if it isn't running, then the game. Put on your headset.".into(),
                            error: false,
                        },
                        Err(e) => Dialog::Message { title: "Couldn't launch".into(), text: e, error: true },
                    });
                }
                let uninstall = egui::Button::new(RichText::new("Uninstall").size(16.0)).min_size(Vec2::new(120.0, 36.0));
                if ui.add_enabled(installed, uninstall).clicked() {
                    self.dialog = Some(Dialog::ConfirmUninstall);
                }
            });

            if let Some(task) = &self.task {
                let t = task.lock().unwrap();
                ui.add_space(10.0);
                let bar = match t.fraction {
                    Some(f) => egui::ProgressBar::new(f).show_percentage(),
                    None => egui::ProgressBar::new(0.0).animate(true),
                };
                ui.add(
                    bar.desired_width(ui.available_width())
                        .fill(GREEN.linear_multiply(0.8)),
                );
                ui.label(RichText::new(&t.line).color(DIM));
            }
        });
    }

    fn dialogs(&mut self, ctx: &egui::Context) {
        let Some(dialog) = &self.dialog else { return };
        let mut close = false;
        let mut action: Option<Action> = None;
        let modal = egui::Modal::new(egui::Id::unique("dialog")).frame(dialog_frame()).show(ctx, |ui| {
            ui.set_max_width(440.0);
            match dialog {
                Dialog::Untested { build } => {
                    ui.heading(RichText::new("Untested game version").color(AMBER));
                    let tested = match &self.manifest {
                        Some(Ok(m)) => crate::tested_list(m),
                        _ => String::new(),
                    };
                    ui.label(format!(
                        "{build} hasn't been tested with this release (tested: {tested}). It may work fine, or the game may crash or misbehave in VR. You can uninstall at any time."
                    ));
                    ui.add_space(8.0);
                    ui.horizontal(|ui| {
                        if ui.button(RichText::new("Install anyway").strong()).clicked() {
                            action = Some(Box::new(|app, ctx| app.start_install(ctx, true, false)));
                        }
                        if ui.button("Cancel").clicked() {
                            close = true;
                        }
                    });
                }
                Dialog::CloseSteam { uninstall } => {
                    ui.heading(RichText::new("Steam needs to restart").color(TEXT));
                    ui.label(if *uninstall {
                        "Steam closes for a moment while the Shroom and Gloom VR entry is removed from your library, then starts again by itself."
                    } else {
                        "Steam closes for a moment while Shroom and Gloom VR is added to your library, then starts again by itself. SteamVR closes with it."
                    });
                    ui.add_space(8.0);
                    let uninstall = *uninstall;
                    ui.horizontal(|ui| {
                        if ui.button(RichText::new("Restart Steam and continue").strong()).clicked() {
                            action = Some(Box::new(move |app, ctx| {
                                if uninstall {
                                    app.start_uninstall(ctx, true)
                                } else {
                                    app.start_install(ctx, true, true)
                                }
                            }));
                        }
                        if !uninstall && ui.button("Install without the library entry").clicked() {
                            action = Some(Box::new(|app, ctx| {
                                app.steam_entry = false;
                                app.start_install(ctx, true, true)
                            }));
                        }
                        if ui.button("Cancel").clicked() {
                            close = true;
                        }
                    });
                }
                Dialog::ConfirmUninstall => {
                    ui.heading(RichText::new("Uninstall ShroomVR?").color(TEXT));
                    ui.label("This removes the mod, its settings, the launcher and the Steam library entry. BepInEx goes too if the installer added it and no other mods use it.");
                    ui.add_space(8.0);
                    ui.horizontal(|ui| {
                        if ui.button(RichText::new("Uninstall").strong().color(RED)).clicked() {
                            action = Some(Box::new(|app, ctx| app.start_uninstall(ctx, false)));
                        }
                        if ui.button("Cancel").clicked() {
                            close = true;
                        }
                    });
                }
                Dialog::Message { title, text, error } => {
                    ui.heading(RichText::new(title).color(if *error { RED } else { GREEN }));
                    ui.label(text);
                    ui.add_space(8.0);
                    if ui.button("OK").clicked() {
                        close = true;
                    }
                }
            }
        });
        if modal.should_close() {
            close = true;
        }
        if let Some(action) = action {
            self.dialog = None;
            action(self, ctx);
        } else if close {
            self.dialog = None;
        }
    }
}

fn message(title: &str, text: &str) -> Dialog {
    Dialog::Message {
        title: title.into(),
        text: text.into(),
        error: true,
    }
}

fn card(ui: &mut egui::Ui, title: &str, contents: impl FnOnce(&mut egui::Ui)) {
    egui::Frame::new()
        .fill(CARD)
        .corner_radius(CornerRadius::same(10))
        .inner_margin(Margin::same(14))
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            ui.label(RichText::new(title).size(18.0).strong().color(TEXT));
            ui.add_space(6.0);
            contents(ui);
        });
}

fn status_line(ui: &mut egui::Ui, color: Color32, text: &str) {
    ui.horizontal_wrapped(|ui| {
        let size = ui.text_style_height(&egui::TextStyle::Body);
        let (rect, _) = ui.allocate_exact_size(Vec2::splat(size), egui::Sense::hover());
        ui.painter()
            .circle_filled(rect.center(), size * 0.22, color);
        ui.label(RichText::new(text).color(if color == DIM { DIM } else { TEXT }));
    });
}

fn footer(ui: &mut egui::Ui) {
    ui.horizontal(|ui| {
        ui.label(RichText::new(format!("Installer {}", crate::VERSION)).color(DIM));
        ui.label(RichText::new("·").color(DIM));
        ui.hyperlink_to("GitHub", format!("https://github.com/{REPO}"));
        ui.label(RichText::new("·").color(DIM));
        if ui.link("Launch log").clicked() {
            let log = std::env::var_os("XDG_STATE_HOME")
                .map(PathBuf::from)
                .unwrap_or_else(|| steam::home().join(".local/state"))
                .join("shroomvr");
            open(&log);
        }
    });
}

fn dialog_frame() -> egui::Frame {
    egui::Frame::new()
        .fill(CARD)
        .stroke(Stroke::new(1.0, Color32::from_rgb(70, 46, 98)))
        .corner_radius(CornerRadius::same(12))
        .inner_margin(Margin::same(18))
}

fn open(path: &Path) {
    let _ = steam::detached(std::process::Command::new("xdg-open").arg(path));
}

fn style(ctx: &egui::Context) {
    let mut visuals = egui::Visuals::dark();
    visuals.panel_fill = BG;
    visuals.window_fill = CARD;
    visuals.override_text_color = Some(TEXT);
    visuals.hyperlink_color = GREEN;
    visuals.selection.bg_fill = GREEN.linear_multiply(0.5);
    visuals.widgets.inactive.weak_bg_fill = Color32::from_rgb(52, 34, 74);
    visuals.widgets.hovered.weak_bg_fill = Color32::from_rgb(70, 46, 98);
    visuals.widgets.active.weak_bg_fill = Color32::from_rgb(86, 58, 118);
    ctx.set_theme(egui::Theme::Dark);
    ctx.set_visuals_of(egui::Theme::Dark, visuals);
    ctx.all_styles_mut(|s| {
        s.spacing.item_spacing = Vec2::new(8.0, 6.0);
        s.spacing.button_padding = Vec2::new(12.0, 6.0);
        for (text_style, size) in [
            (egui::TextStyle::Body, 15.0),
            (egui::TextStyle::Button, 15.0),
            (egui::TextStyle::Heading, 20.0),
        ] {
            if let Some(font) = s.text_styles.get_mut(&text_style) {
                font.size = size;
            }
        }
    });
}
