//! Library art for the "Shroom and Gloom VR" Steam entry: the game's own art (from Steam's cache,
//! upgraded to the full size versions on Steam's CDN) with a VR badge on it.

use crate::steam::{APP_ID, Steam};
use image::imageops::{self, FilterType};
use image::{DynamicImage, ImageFormat, RgbaImage};
use std::fs;
use std::path::Path;

const BADGE: &[u8] = include_bytes!("../assets/vr-badge.png");
pub const ICON: &[u8] = include_bytes!("../assets/icon.png");
const CDN: &str = "https://shared.steamstatic.com/store_item_assets/steam/apps";

#[derive(Default)]
pub struct Art {
    pub portrait: Option<DynamicImage>,
    pub wide: Option<DynamicImage>,
    pub hero: Option<DynamicImage>,
    pub logo: Option<DynamicImage>,
}

impl Art {
    pub fn load(steam: &Steam) -> Art {
        Art {
            portrait: fetch(steam, "library_600x900.jpg", "library_600x900_2x.jpg"),
            wide: fetch(steam, "library_header.jpg", "library_header_2x.jpg"),
            hero: fetch(steam, "library_hero.jpg", "library_hero_2x.jpg"),
            logo: fetch(steam, "logo.png", "logo_2x.png"),
        }
    }

    /// Writes the Steam grid images for a shortcut into `grid` (userdata/<user>/config/grid).
    pub fn write_grid(&self, grid: &Path, shortcut_id: u32) -> Result<(), String> {
        fs::create_dir_all(grid).map_err(|e| e.to_string())?;
        let save = |img: &DynamicImage, name: String, format: ImageFormat| {
            img.save_with_format(grid.join(name), format)
                .map_err(|e| e.to_string())
        };
        if let Some(p) = &self.portrait {
            save(
                &with_badge(p, 0.12, 0.04, 0.03),
                format!("{shortcut_id}p.jpg"),
                ImageFormat::Jpeg,
            )?;
        }
        if let Some(w) = &self.wide {
            save(
                &with_badge(w, 0.21, 0.025, 0.04),
                format!("{shortcut_id}.jpg"),
                ImageFormat::Jpeg,
            )?;
        }
        if let Some(h) = &self.hero {
            save(h, format!("{shortcut_id}_hero.jpg"), ImageFormat::Jpeg)?;
        }
        if let Some(l) = &self.logo {
            save(
                &logo_with_badge(l),
                format!("{shortcut_id}_logo.png"),
                ImageFormat::Png,
            )?;
        }
        Ok(())
    }

    /// A square icon from the capsule art (the mushroom's face), or the installer's own icon.
    pub fn icon(&self) -> DynamicImage {
        match &self.portrait {
            Some(p) if p.width() >= 300 => {
                let s = p.width() as f32 / 600.0;
                let face = p.crop_imm(
                    (215.0 * s) as u32,
                    (70.0 * s) as u32,
                    (240.0 * s) as u32,
                    (240.0 * s) as u32,
                );
                face.resize_exact(256, 256, FilterType::Lanczos3)
            }
            _ => image::load_from_memory(ICON).expect("bundled icon"),
        }
    }

    /// The header image for the installer window: hero art with the logo on its left.
    pub fn banner(&self, width: u32, height: u32) -> Option<RgbaImage> {
        let hero = self.hero.as_ref()?;
        let mut banner = hero
            .resize_to_fill(width, height, FilterType::Triangle)
            .to_rgba8();
        if let Some(logo) = &self.logo {
            let logo = logo_with_badge(logo);
            let logo = logo.resize(width * 2 / 5, height * 9 / 10, FilterType::Triangle);
            imageops::overlay(
                &mut banner,
                &logo,
                (width / 24) as i64,
                ((height - logo.height()) / 2) as i64,
            );
        }
        Some(banner)
    }
}

/// Steam's cached copy of the art, or its full size version from the CDN when there is one.
fn fetch(steam: &Steam, cached: &str, full_size: &str) -> Option<DynamicImage> {
    let local = steam.cached_art(APP_ID, cached);
    let mut urls = Vec::new();
    if let Some((_, Some(hash))) = &local {
        urls.push(format!("{CDN}/{APP_ID}/{hash}/{full_size}"));
    }
    urls.push(format!("{CDN}/{APP_ID}/{full_size}"));
    for url in urls {
        let Ok(mut response) = ureq::get(&url)
            .header("User-Agent", "shroomvr-installer")
            .call()
        else {
            continue;
        };
        let Ok(bytes) = response
            .body_mut()
            .with_config()
            .limit(32 << 20)
            .read_to_vec()
        else {
            continue;
        };
        if let Ok(img) = image::load_from_memory(&bytes) {
            return Some(img);
        }
    }
    let (path, _) = local?;
    image::open(path).ok()
}

/// The badge in the bottom right corner, `height` as a fraction of the image height.
fn with_badge(img: &DynamicImage, height: f32, right: f32, bottom: f32) -> DynamicImage {
    let mut out = img.to_rgba8();
    let badge = badge((out.height() as f32 * height) as u32);
    let x = out.width() as i64 - badge.width() as i64 - (out.width() as f32 * right) as i64;
    let y = out.height() as i64 - badge.height() as i64 - (out.height() as f32 * bottom) as i64;
    imageops::overlay(&mut out, &badge, x, y);
    DynamicImage::ImageRgba8(out)
}

/// The badge tucked under the logo's bottom right corner.
fn logo_with_badge(logo: &DynamicImage) -> DynamicImage {
    let mut out = logo.to_rgba8();
    let (mut x0, mut y0, mut x1, mut y1) = (out.width(), out.height(), 0, 0);
    for (x, y, p) in out.enumerate_pixels() {
        if p[3] > 40 {
            x0 = x0.min(x);
            y0 = y0.min(y);
            x1 = x1.max(x);
            y1 = y1.max(y);
        }
    }
    if x1 <= x0 || y1 <= y0 {
        return DynamicImage::ImageRgba8(out);
    }
    let badge = badge(((y1 - y0) as f32 * 0.26) as u32);
    let x = (x1 as i64 - (badge.width() as f32 * 0.55) as i64)
        .min((out.width() - badge.width()) as i64);
    let y = (y1 as i64 - (badge.height() as f32 * 0.62) as i64)
        .min((out.height() - badge.height()) as i64);
    imageops::overlay(&mut out, &badge, x, y);
    DynamicImage::ImageRgba8(out)
}

fn badge(height: u32) -> RgbaImage {
    let badge = image::load_from_memory(BADGE).expect("bundled badge");
    let width = badge.width() * height.max(1) / badge.height();
    badge
        .resize_exact(width.max(1), height.max(1), FilterType::Lanczos3)
        .to_rgba8()
}
