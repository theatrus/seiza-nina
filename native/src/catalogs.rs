//! Host-facing catalog installation using Seiza's manifest, verification and cache.
use super::{c_string, read_json};
use seiza_download::{CachePolicy, CatalogManager, CatalogSet, Dataset, DownloadEvent};
use serde::{Deserialize, Serialize};
use std::ffi::{CString, c_char, c_void};
use std::path::{Path, PathBuf};
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};
use std::time::Duration;

type ProgressCallback = Option<unsafe extern "C" fn(*const c_char, *mut c_void)>;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct InstallRequest {
    directory: PathBuf,
    package: String,
}

fn selection(package: &str) -> Result<CatalogSet, String> {
    match package {
        "standard" => Ok(CatalogSet::dataset(Dataset::StarsDeepGaia17).with(Dataset::BlindGaia16)),
        "compact" => Ok(CatalogSet::dataset(Dataset::StarsLiteTycho2)),
        "deep" => Ok(CatalogSet::dataset(Dataset::StarsDeepGaia20).with(Dataset::BlindGaia16)),
        _ => Err("Unknown catalog package".into()),
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Progress {
    message: String,
    downloaded: u64,
    total: u64,
}

fn event_progress(event: DownloadEvent) -> Progress {
    let (message, downloaded, total) = match event {
        DownloadEvent::FetchingManifest { .. } => ("Fetching catalog manifest".into(), 0, 0),
        DownloadEvent::UsingCachedManifest { .. } => ("Reading catalog manifest".into(), 0, 0),
        DownloadEvent::CacheHit { name, .. } => (format!("Using cached {name}"), 0, 0),
        DownloadEvent::DownloadStarted { name, bytes } => (format!("Downloading {name}"), 0, bytes),
        DownloadEvent::DownloadProgress {
            name,
            downloaded,
            total,
            ..
        } => (format!("Downloading {name}"), downloaded, total),
        DownloadEvent::DownloadComplete { name, .. } => (format!("Downloaded {name}"), 0, 0),
        DownloadEvent::Verifying { name } => (format!("Verifying {name}"), 0, 0),
        DownloadEvent::Installing { name, .. } => (format!("Preparing {name}"), 0, 0),
        DownloadEvent::InstallComplete { name, .. } => (format!("Prepared {name}"), 0, 0),
    };
    Progress {
        message,
        downloaded,
        total,
    }
}

fn check_cancel(flag: &AtomicBool) -> Result<(), String> {
    if flag.load(Ordering::Relaxed) {
        Err("Catalog installation cancelled".into())
    } else {
        Ok(())
    }
}

async fn cancelled(flag: &AtomicBool) {
    while !flag.load(Ordering::Relaxed) {
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

async fn install_with_manager(
    manager: CatalogManager,
    request: InstallRequest,
    flag: Arc<AtomicBool>,
    report: impl Fn(Progress) + Send + Sync,
) -> Result<PathBuf, String> {
    check_cancel(&flag)?;
    let set = selection(&request.package)?;
    if !request.directory.is_absolute() {
        return Err("Choose an absolute catalog installation directory".into());
    }
    let download = async {
        let bundle = manager
            .ensure_with(&set, |e| report(event_progress(e)))
            .await
            .map_err(|e| e.to_string())?;
        // Recheck cached objects too: never publish a damaged same-size cache hit.
        bundle
            .verify_with(|e| report(event_progress(e)))
            .await
            .map_err(|e| e.to_string())?;
        Ok::<_, String>(bundle)
    };
    let bundle = tokio::select! {
        result = download => result?,
        () = cancelled(&flag) => return Err("Catalog installation cancelled".into()),
    };
    check_cancel(&flag)?;
    tokio::fs::create_dir_all(&request.directory)
        .await
        .map_err(|e| e.to_string())?;
    let stage = tempfile::Builder::new()
        .prefix(".seiza-install-")
        .tempdir_in(&request.directory)
        .map_err(|e| e.to_string())?;
    // Complete the current filesystem operation before observing cancellation.
    // Materializing to a private sibling keeps existing catalogs intact on copy errors.
    let paths = bundle
        .materialize_with(stage.path(), |e| report(event_progress(e)))
        .await
        .map_err(|e| e.to_string())?;
    check_cancel(&flag)?;
    for source in paths {
        check_cancel(&flag)?;
        let name = source.file_name().ok_or("Missing catalog file name")?;
        let target = request.directory.join(name);
        seiza_download::bundle::replace_file(&source, &target)
            .await
            .map_err(|e| e.to_string())?;
    }
    Ok(request.directory)
}

/// # Safety
/// JSON is NUL-terminated UTF-8; cancel remains live until return. Callback and
/// context remain live for every synchronous progress invocation until return.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_nina_install_catalogs(
    json: *const c_char,
    cancel: *const Arc<AtomicBool>,
    callback: ProgressCallback,
    context: *mut c_void,
) -> *mut c_char {
    let context = context as usize;
    let result = std::panic::catch_unwind(|| {
        let flag = unsafe { cancel.as_ref() }.ok_or("Missing cancellation signal")?;
        let request: InstallRequest = unsafe { read_json(json) }?;
        check_cancel(flag)?;
        let manager = CatalogManager::builder()
            .policy(CachePolicy::ForceRefresh)
            .build()
            .map_err(|e| e.to_string())?;
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .map_err(|e| e.to_string())?;
        runtime.block_on(install_with_manager(
            manager,
            request,
            Arc::clone(flag),
            move |progress| {
                if let Some(callback) = callback {
                    let json = CString::new(
                        serde_json::to_string(&progress).expect("serializable progress"),
                    )
                    .expect("JSON has no NUL");
                    unsafe {
                        callback(json.as_ptr(), context as *mut c_void);
                    }
                }
            },
        ))
    })
    .unwrap_or_else(|_| Err("Catalog installer panicked".into()));
    c_string(match result {
        Ok(directory) => serde_json::json!({"success": true, "directory": directory}).to_string(),
        Err(message) => serde_json::json!({"success": false, "message": message}).to_string(),
    })
}

/// # Safety
/// JSON is NUL-terminated UTF-8 with catalogPath and indexPath strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_nina_catalog_status(json: *const c_char) -> *mut c_char {
    let result = std::panic::catch_unwind(|| -> Result<_, String> {
        let options: super::Options = unsafe { read_json(json) }?;
        let arg = (!options.catalog_path.is_empty()).then(|| Path::new(&options.catalog_path));
        let stars = seiza::data_paths::star_data(arg).ok();
        let index_arg = (!options.index_path.is_empty()).then(|| Path::new(&options.index_path));
        let index = match &stars {
            Some(path) => seiza::data_paths::blind_index_beside(index_arg, path)
                .ok()
                .flatten(),
            None => seiza::data_paths::blind_index(index_arg).ok().flatten(),
        };
        Ok(serde_json::json!({"starCatalog": stars, "blindIndex": index}))
    })
    .unwrap_or_else(|_| Err("Catalog lookup failed".into()));
    c_string(match result {
        Ok(status) => status.to_string(),
        Err(message) => serde_json::json!({"message": message}).to_string(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use sha2::{Digest, Sha256};

    fn cached_manager(root: &Path, corrupt: bool) -> CatalogManager {
        let bytes = b"artificial catalog test bytes";
        let hash = seiza_download::bundle::lowercase_hex(&Sha256::digest(bytes));
        let files: Vec<_> = seiza_download::REQUIRED_BUNDLE_FILES
            .iter()
            .map(|name| {
                serde_json::json!({
                    "name": name, "bytes": bytes.len(), "sha256": hash,
                })
            })
            .collect();
        let manifest = serde_json::json!({"version": "catalog-bundle-v2-test", "files": files});
        std::fs::create_dir_all(root.join("manifests")).unwrap();
        // See CatalogManager's documented cache layout.
        std::fs::write(
            root.join("manifests").join("catalog-bundle-v2.json"),
            manifest.to_string(),
        )
        .unwrap();
        for name in seiza_download::REQUIRED_BUNDLE_FILES {
            let dir = root.join("objects").join(&hash);
            std::fs::create_dir_all(&dir).unwrap();
            let data = if corrupt {
                vec![0; bytes.len()]
            } else {
                bytes.to_vec()
            };
            std::fs::write(dir.join(name), data).unwrap();
        }
        CatalogManager::builder()
            .cache_dir(root)
            .base_url(seiza_download::LEGACY_V2_BUNDLE_BASE_URL)
            .policy(CachePolicy::OfflineOnly)
            .build()
            .unwrap()
    }

    #[test]
    fn installs_only_solver_files_from_verified_cache() {
        let dir = tempfile::tempdir().unwrap();
        let manager = cached_manager(&dir.path().join("cache"), false);
        let output = dir.path().join("output");
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        let result = runtime.block_on(install_with_manager(
            manager,
            InstallRequest {
                directory: output.clone(),
                package: "standard".into(),
            },
            Arc::default(),
            |_| {},
        ));
        result.unwrap();
        let mut names: Vec<_> = std::fs::read_dir(&output)
            .unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        names.sort();
        assert_eq!(names, ["blind-gaia16.idx", "stars-deep-gaia17.bin"]);
    }

    #[test]
    fn cancellation_and_bad_hash_leave_existing_catalog_untouched() {
        for cancel in [false, true] {
            let dir = tempfile::tempdir().unwrap();
            let manager = cached_manager(&dir.path().join("cache"), true);
            let output = dir.path().join("output");
            std::fs::create_dir_all(&output).unwrap();
            std::fs::write(output.join("stars-lite-tycho2.bin"), b"original").unwrap();
            let runtime = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .unwrap();
            let result = runtime.block_on(install_with_manager(
                manager,
                InstallRequest {
                    directory: output.clone(),
                    package: "compact".into(),
                },
                Arc::new(AtomicBool::new(cancel)),
                |_| {},
            ));
            assert!(result.is_err());
            assert_eq!(
                std::fs::read(output.join("stars-lite-tycho2.bin")).unwrap(),
                b"original"
            );
        }
    }
}
