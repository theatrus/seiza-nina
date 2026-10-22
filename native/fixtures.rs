//! Deterministic, artificial sky. No network or user image/catalog is needed by tests.
use seiza::blind::{BlindIndex, BlindParams};
use seiza::catalog::tiles::{TileCatalog, TileSetBuilder};
use std::path::Path;

pub const WIDTH: u32 = 1024;
pub const HEIGHT: u32 = 768;

pub fn create(directory: &Path, rotation: f64, flipped: bool) -> Vec<u16> {
    std::fs::create_dir_all(directory).unwrap();
    let truth = seiza::Wcs::from_center_scale_rotation(
        (120.0, 25.0),
        seiza::image_center(WIDTH, HEIGHT),
        10.0,
        rotation,
        flipped,
    );
    let mut builder = TileSetBuilder::new(45, 2016.0, "Artificial test stars; CC0");
    let mut seed = 42u64;
    let mut next = || {
        seed = seed
            .wrapping_mul(6364136223846793005)
            .wrapping_add(1442695040888963407);
        (seed >> 11) as f64 / (1u64 << 53) as f64
    };
    let mut pixels: Vec<f64> = (0..WIDTH * HEIGHT)
        .map(|_| 1000.0 + next() * 30.0)
        .collect();
    for _ in 0..1800 {
        let x = (next() * 3.0 - 1.0) * f64::from(WIDTH);
        let y = (next() * 3.0 - 1.0) * f64::from(HEIGHT);
        let mag = 5.0 + next() * 6.0;
        let (ra, dec) = truth.pixel_to_world(x, y);
        builder.add(ra, dec, mag as f32);
        if x < 8.0 || y < 8.0 || x >= f64::from(WIDTH) - 8.0 || y >= f64::from(HEIGHT) - 8.0 {
            continue;
        }
        let peak = 35000.0 * 10f64.powf(-0.4 * (mag - 5.0));
        for dy in -6..=6 {
            for dx in -6..=6 {
                let (ix, iy) = (x.round() as i32 + dx, y.round() as i32 + dy);
                let r2 = (f64::from(ix) - x).powi(2) + (f64::from(iy) - y).powi(2);
                pixels[(iy as u32 * WIDTH + ix as u32) as usize] +=
                    peak * (-r2 / (2.0 * 1.5f64.powi(2))).exp();
            }
        }
    }
    let path = directory.join("stars.bin");
    builder.write_to(&path).unwrap();
    let catalog = TileCatalog::open(&path).unwrap();
    let index = BlindIndex::build(&catalog, &BlindParams::default());
    index.write_to(&directory.join("blind-gaia16.idx")).unwrap();
    let pixels: Vec<u16> = pixels
        .into_iter()
        .map(|v| v.clamp(0.0, 65535.0) as u16)
        .collect();
    std::fs::write(
        directory.join("image.raw"),
        pixels
            .iter()
            .flat_map(|v| v.to_le_bytes())
            .collect::<Vec<_>>(),
    )
    .unwrap();
    pixels
}
