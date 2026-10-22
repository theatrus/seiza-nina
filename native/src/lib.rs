//! Thin host adapter. Detection, catalog access and all astrometry belong to Seiza.
mod catalogs;
use seiza::blind::{BlindIndex, BlindParams, solve_blind_with_limits};
use seiza::catalog::tiles::TileCatalog;
use seiza::solve::{SolveHint, SolveLimits, solve_with_limits};
use serde::{Deserialize, Serialize};
use std::ffi::{CStr, CString, c_char};
use std::path::Path;
use std::ptr;
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};
use std::time::Duration;

pub struct Engine {
    catalog: TileCatalog,
    index_path: Option<std::path::PathBuf>,
    index: Option<BlindIndex>,
    pool: rayon::ThreadPool,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Options {
    catalog_path: String,
    index_path: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Request {
    center: Option<(f64, f64)>,
    scale: f64,
    search_radius: f64,
    scale_tolerance: f64,
    min_scale: f64,
    max_scale: f64,
    time_limit_seconds: f64,
    downsample: u32,
    max_stars: usize,
}

impl Request {
    fn validate(&self) -> Result<(), String> {
        if !(self.scale_tolerance.is_finite()
            && self.scale_tolerance > 0.0
            && self.scale_tolerance < 1.0
            && self.min_scale.is_finite()
            && self.min_scale > 0.0
            && self.max_scale.is_finite()
            && self.max_scale >= self.min_scale
            && self.time_limit_seconds.is_finite()
            && self.time_limit_seconds > 0.0
            && self.time_limit_seconds <= 3600.0
            && (1..=8).contains(&self.downsample)
            && (32..=2000).contains(&self.max_stars))
        {
            return Err("Invalid Seiza solve settings".into());
        }
        if let Some((ra, dec)) = self.center
            && !(ra.is_finite()
                && (0.0..360.0).contains(&ra)
                && dec.is_finite()
                && (-90.0..=90.0).contains(&dec)
                && self.scale.is_finite()
                && self.scale > 0.0
                && self.search_radius.is_finite()
                && (0.0..=180.0).contains(&self.search_radius))
        {
            return Err("Invalid pointing or optical scale".into());
        }
        Ok(())
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Response {
    success: bool,
    message: String,
    center: (f64, f64),
    scale: f64,
    radius: f64,
    crpix: (f64, f64),
    cd: [[f64; 2]; 2],
    matched_stars: usize,
    rms_arcsec: f64,
}

impl Engine {
    fn open(options: Options) -> Result<Self, String> {
        let explicit =
            (!options.catalog_path.trim().is_empty()).then(|| Path::new(&options.catalog_path));
        let path = seiza::data_paths::star_data(explicit).map_err(|e| e.to_string())?;
        let catalog = TileCatalog::open(&path).map_err(|e| e.to_string())?;
        let index_arg =
            (!options.index_path.trim().is_empty()).then(|| Path::new(&options.index_path));
        // A missing index is allowed for primary solving. Resolve/open it on the first blind solve.
        let index_path = if let Some(arg) = index_arg {
            Some(arg.to_path_buf())
        } else {
            seiza::data_paths::blind_index_beside(None, &path).map_err(|e| e.to_string())?
        };
        let threads = std::thread::available_parallelism().map_or(1, |n| n.get());
        let pool = rayon::ThreadPoolBuilder::new()
            .num_threads(threads)
            .build()
            .map_err(|e| e.to_string())?;
        Ok(Self {
            catalog,
            index_path,
            index: None,
            pool,
        })
    }

    fn solve(
        &mut self,
        pixels: &[u16],
        width: u32,
        height: u32,
        request: Request,
        flag: Arc<AtomicBool>,
    ) -> Result<Response, String> {
        request.validate()?;
        if width == 0
            || height == 0
            || width.checked_mul(height).map(|n| n as usize) != Some(pixels.len())
        {
            return Err("Image dimensions do not match its pixel buffer".into());
        }
        let limits = SolveLimits::within(Duration::from_secs_f64(request.time_limit_seconds))
            .with_cancel(seiza::CancelSignal::from(flag));
        limits.check().map_err(|e| e.to_string())?;
        if request.center.is_none() && self.index.is_none() {
            let path = seiza::data_paths::blind_index(self.index_path.as_deref())
                .map_err(|e| e.to_string())?
                .ok_or("Install Seiza's blind index with `seiza setup` before blind solving")?;
            self.index = Some(BlindIndex::open(&path).map_err(|e| e.to_string())?);
        }
        limits.check().map_err(|e| e.to_string())?;
        let factor = request.downsample.min(width).min(height);
        let (luma, dw, dh) = downsample(pixels, width, height, factor, &limits)?;
        let config = seiza::DetectConfig {
            max_stars: request.max_stars,
            ..Default::default()
        };
        let mut stars = self
            .pool
            .install(|| seiza::detect_stars_luma_f32(&luma, dw, dh, &config));
        // Restore original-image coordinates, including the centre of each averaged block.
        for star in &mut stars {
            star.x = (star.x + 0.5) * f64::from(factor) - 0.5;
            star.y = (star.y + 0.5) * f64::from(factor) - 0.5;
        }
        limits.check().map_err(|e| e.to_string())?;
        let solution = self
            .pool
            .install(|| {
                if let Some(center) = request.center {
                    solve_with_limits(
                        &stars,
                        &self.catalog,
                        &SolveHint {
                            center,
                            radius_deg: request.search_radius,
                            scale_arcsec_px: request.scale,
                            scale_tolerance: request.scale_tolerance,
                            sip_order: 0,
                        },
                        (width, height),
                        &limits,
                    )
                } else {
                    solve_blind_with_limits(
                        &stars,
                        &self.catalog,
                        self.index.as_ref().expect("opened above"),
                        &BlindParams {
                            min_scale_arcsec_px: request.min_scale,
                            max_scale_arcsec_px: request.max_scale,
                            ..Default::default()
                        },
                        (width, height),
                        &limits,
                    )
                }
            })
            .map_err(|e| e.to_string())?;
        limits.check().map_err(|e| e.to_string())?;
        let wcs = solution.wcs;
        let scale = wcs.scale_arcsec_per_px();
        let middle = seiza::image_center(width, height);
        Ok(Response {
            success: true,
            message: String::new(),
            center: wcs.pixel_to_world(middle.0, middle.1),
            scale,
            radius: f64::from(width).hypot(f64::from(height)) * scale / 7200.0,
            crpix: wcs.crpix,
            cd: wcs.cd,
            matched_stars: solution.matched_stars,
            rms_arcsec: solution.rms_arcsec,
        })
    }
}

fn downsample(
    pixels: &[u16],
    width: u32,
    height: u32,
    factor: u32,
    limits: &SolveLimits,
) -> Result<(Vec<f32>, u32, u32), String> {
    let (dw, dh) = (width / factor, height / factor);
    let mut out = Vec::with_capacity((dw * dh) as usize);
    for y in 0..dh {
        limits.check().map_err(|e| e.to_string())?;
        for x in 0..dw {
            let mut sum = 0u64;
            for by in 0..factor {
                for bx in 0..factor {
                    sum +=
                        u64::from(pixels[((y * factor + by) * width + x * factor + bx) as usize]);
                }
            }
            out.push(sum as f32 / (factor * factor) as f32 / 65535.0);
        }
    }
    Ok((out, dw, dh))
}

fn c_string(value: String) -> *mut c_char {
    CString::new(value.replace('\0', " "))
        .expect("NUL removed")
        .into_raw()
}

unsafe fn read_json<T: serde::de::DeserializeOwned>(input: *const c_char) -> Result<T, String> {
    if input.is_null() {
        return Err("Missing JSON request".into());
    }
    serde_json::from_slice(unsafe { CStr::from_ptr(input) }.to_bytes()).map_err(|e| e.to_string())
}

/// # Safety
/// `json` is NUL-terminated UTF-8; `error` points to writable pointer storage.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_nina_open(
    json: *const c_char,
    error: *mut *mut c_char,
) -> *mut Engine {
    if error.is_null() {
        return ptr::null_mut();
    }
    unsafe {
        *error = ptr::null_mut();
    }
    let result = std::panic::catch_unwind(|| unsafe { read_json(json) }.and_then(Engine::open));
    match result.unwrap_or_else(|_| Err("Native engine panicked while opening catalogs".into())) {
        Ok(engine) => Box::into_raw(Box::new(engine)),
        Err(message) => {
            unsafe {
                *error = c_string(message);
            }
            ptr::null_mut()
        }
    }
}

/// # Safety
/// Engine is live and exclusively held; pixels contains `length` samples; JSON and cancel are live.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_nina_solve(
    engine: *mut Engine,
    pixels: *const u16,
    length: usize,
    width: u32,
    height: u32,
    json: *const c_char,
    cancel: *const Arc<AtomicBool>,
) -> *mut c_char {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if engine.is_null()
            || pixels.is_null()
            || cancel.is_null()
            || length > isize::MAX as usize / 2
        {
            return Err("Invalid native solve arguments".into());
        }
        unsafe { &mut *engine }.solve(
            unsafe { std::slice::from_raw_parts(pixels, length) },
            width,
            height,
            unsafe { read_json(json) }?,
            Arc::clone(unsafe { &*cancel }),
        )
    }))
    .unwrap_or_else(|_| Err("Native solver panicked".into()));
    c_string(match result {
        Ok(response) => serde_json::to_string(&response).expect("finite solve result"),
        Err(message) => serde_json::json!({"success": false, "message": message}).to_string(),
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn seiza_nina_cancel_new() -> *mut Arc<AtomicBool> {
    Box::into_raw(Box::new(Arc::new(AtomicBool::new(false))))
}

/// # Safety
/// The signal is live until this call completes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_nina_cancel_set(signal: *const Arc<AtomicBool>) {
    if let Some(signal) = unsafe { signal.as_ref() } {
        signal.store(true, Ordering::Relaxed);
    }
}

/// # Safety
/// Signal is null or a live allocation, freed only once after solve and cancellation registration finish.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_nina_cancel_free(signal: *mut Arc<AtomicBool>) {
    if !signal.is_null() {
        unsafe {
            drop(Box::from_raw(signal));
        }
    }
}

/// # Safety
/// Engine is null or a live allocation with no active solve, freed only once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_nina_close(engine: *mut Engine) {
    if !engine.is_null() {
        unsafe {
            drop(Box::from_raw(engine));
        }
    }
}

/// # Safety
/// String is null or an owned string returned by this library, freed only once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_nina_string_free(value: *mut c_char) {
    if !value.is_null() {
        unsafe {
            drop(CString::from_raw(value));
        }
    }
}

#[cfg(test)]
#[path = "../fixtures.rs"]
mod fixtures;

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::OnceLock;

    fn fixture() -> &'static (tempfile::TempDir, Vec<u16>) {
        static FIXTURE: OnceLock<(tempfile::TempDir, Vec<u16>)> = OnceLock::new();
        FIXTURE.get_or_init(|| {
            let dir = tempfile::tempdir().unwrap();
            let pixels = fixtures::create(dir.path(), 30.0, false);
            (dir, pixels)
        })
    }

    fn engine() -> Engine {
        Engine::open(Options {
            catalog_path: fixture().0.path().to_string_lossy().into(),
            index_path: String::new(),
        })
        .unwrap()
    }

    fn request(blind: bool, factor: u32) -> Request {
        Request {
            center: (!blind).then_some((120.1, 25.1)),
            scale: 10.0,
            search_radius: 1.0,
            scale_tolerance: 0.25,
            min_scale: 8.0,
            max_scale: 12.0,
            time_limit_seconds: 30.0,
            downsample: factor,
            max_stars: 600,
        }
    }

    #[test]
    fn solves_real_pixels_with_pointing_and_restores_downsampled_coordinates() {
        let mut engine = engine();
        for factor in [1, 2, 3] {
            let result = engine
                .solve(
                    &fixture().1,
                    fixtures::WIDTH,
                    fixtures::HEIGHT,
                    request(false, factor),
                    Arc::default(),
                )
                .unwrap();
            assert!(
                (result.center.0 - 120.0).abs() < 0.002,
                "RA: {:?}",
                result.center
            );
            assert!((result.center.1 - 25.0).abs() < 0.002);
            assert!((result.scale - 10.0).abs() < 0.02);
            assert!(result.matched_stars > 30);
        }
    }

    #[test]
    fn blind_solves_and_reuses_the_loaded_index() {
        let mut engine = engine();
        assert!(engine.index.is_none());
        for _ in 0..2 {
            let result = engine
                .solve(
                    &fixture().1,
                    fixtures::WIDTH,
                    fixtures::HEIGHT,
                    request(true, 1),
                    Arc::default(),
                )
                .unwrap();
            assert!((result.center.0 - 120.0).abs() < 0.002);
            assert!(result.matched_stars >= 12);
            assert!(engine.index.is_some());
        }
    }

    #[test]
    fn recovers_a_rotated_mirrored_field() {
        let dir = tempfile::tempdir().unwrap();
        let pixels = fixtures::create(dir.path(), 150.0, true);
        let mut engine = Engine::open(Options {
            catalog_path: dir.path().to_string_lossy().into(),
            index_path: String::new(),
        })
        .unwrap();
        let result = engine
            .solve(
                &pixels,
                fixtures::WIDTH,
                fixtures::HEIGHT,
                request(false, 2),
                Arc::default(),
            )
            .unwrap();
        let truth = seiza::Wcs::from_center_scale_rotation(
            (120.0, 25.0),
            seiza::image_center(fixtures::WIDTH, fixtures::HEIGHT),
            10.0,
            150.0,
            true,
        );
        for row in 0..2 {
            for col in 0..2 {
                assert!((result.cd[row][col] - truth.cd[row][col]).abs() < 1e-5);
            }
        }
    }

    #[test]
    fn primary_does_not_need_a_blind_index() {
        let mut engine = Engine::open(Options {
            catalog_path: fixture().0.path().to_string_lossy().into(),
            index_path: "missing-test-index.idx".into(),
        })
        .unwrap();
        assert!(
            engine
                .solve(
                    &fixture().1,
                    fixtures::WIDTH,
                    fixtures::HEIGHT,
                    request(false, 1),
                    Arc::default()
                )
                .is_ok()
        );
        let error = engine
            .solve(
                &fixture().1,
                fixtures::WIDTH,
                fixtures::HEIGHT,
                request(true, 1),
                Arc::default(),
            )
            .err()
            .unwrap();
        assert!(error.contains("does not exist"));
    }

    #[test]
    fn solving_pool_uses_all_available_cores() {
        assert_eq!(
            engine().pool.current_num_threads(),
            std::thread::available_parallelism().map_or(1, |n| n.get())
        );
    }

    #[test]
    fn cancelled_work_never_returns_a_solution() {
        let error = engine()
            .solve(
                &fixture().1,
                fixtures::WIDTH,
                fixtures::HEIGHT,
                request(false, 1),
                Arc::new(AtomicBool::new(true)),
            )
            .err()
            .unwrap();
        assert!(error.contains("cancelled"));
    }

    #[test]
    fn validates_dimensions_and_options_before_processing() {
        assert!(
            engine()
                .solve(&[0], 2, 2, request(false, 1), Arc::default())
                .is_err()
        );
        let mut invalid = request(false, 1);
        invalid.time_limit_seconds = f64::NAN;
        assert!(invalid.validate().is_err());
        invalid = request(false, 1);
        invalid.downsample = 0;
        assert!(invalid.validate().is_err());
    }

    #[test]
    fn wrong_hint_and_empty_images_fail_without_trusting_headers() {
        let mut req = request(false, 1);
        req.center = Some((0.0, -45.0));
        assert!(
            engine()
                .solve(
                    &fixture().1,
                    fixtures::WIDTH,
                    fixtures::HEIGHT,
                    req,
                    Arc::default()
                )
                .is_err()
        );
        let blank = vec![1000; (fixtures::WIDTH * fixtures::HEIGHT) as usize];
        assert!(
            engine()
                .solve(
                    &blank,
                    fixtures::WIDTH,
                    fixtures::HEIGHT,
                    request(false, 1),
                    Arc::default()
                )
                .is_err()
        );
    }

    #[test]
    fn timeout_applies_to_preparation_too() {
        let mut req = request(false, 1);
        req.time_limit_seconds = 1e-9;
        let error = engine()
            .solve(
                &fixture().1,
                fixtures::WIDTH,
                fixtures::HEIGHT,
                req,
                Arc::default(),
            )
            .err()
            .unwrap();
        assert!(error.contains("time limit"));
    }

    #[test]
    fn ffi_returns_owned_errors_and_tolerates_null_cleanup() {
        unsafe {
            let mut error = ptr::null_mut();
            let json = CString::new("{}").unwrap();
            assert!(seiza_nina_open(json.as_ptr(), &mut error).is_null());
            assert!(!error.is_null());
            seiza_nina_string_free(error);
            seiza_nina_string_free(ptr::null_mut());
            seiza_nina_close(ptr::null_mut());
            seiza_nina_cancel_free(ptr::null_mut());
        }
    }
}
