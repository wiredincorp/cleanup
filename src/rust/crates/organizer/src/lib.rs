//! Folder organizer.
//!
//! [`plan`] is a dry run: it lists every proposed move, every file that will be
//! left alone and why, and resolves name collisions by renaming. Nothing on
//! disk changes. [`apply`] re-validates each approved move, records the batch
//! in the journal before touching anything, and never overwrites a file.
//! [`undo`] moves files back only when they are unchanged and their original
//! name is still free.
//!
//! Only files directly inside the chosen folder are considered; subfolders are
//! never entered, so the organizer cannot loop into folders it created.
//!
//! Photo and video sorting is the same plan with [`PlanRequest::media`] set:
//! only that kind of file moves, into date folders built from the date it was
//! taken or recorded (see [`media`]), or its last-write date when the file
//! does not say.

pub mod category;
pub mod media;

use std::collections::HashSet;
use std::fs::{self, Metadata};
use std::os::windows::fs::MetadataExt;
use std::path::{Path, PathBuf};

use cleanup_core::files::LockedFile;
use cleanup_core::paths::{
    DirectoryGuard, KnownFolder, describe_file_error, is_drive_root, is_plain_absolute, is_within,
    known_folder, same_path,
};
use cleanup_core::time::civil_from_days;
use cleanup_core::{CoreError, ErrorCode, Result};
use cleanup_operations::{
    BatchKind, Event, FileIdentity, Journal, MoveOutcome, PlannedMove, UndoOutcome, new_batch_id,
    organize_batch,
};
use serde::{Deserialize, Serialize};
use windows_sys::Win32::Foundation::FILETIME;
use windows_sys::Win32::Storage::FileSystem::{
    FILE_ATTRIBUTE_HIDDEN, FILE_ATTRIBUTE_OFFLINE, FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS,
    FILE_ATTRIBUTE_RECALL_ON_OPEN, FILE_ATTRIBUTE_REPARSE_POINT, FILE_ATTRIBUTE_SYSTEM,
    FileTimeToLocalFileTime,
};

use crate::category::{categorize, is_partial_download, is_shortcut, simple_group};
use crate::media::{MediaKind, capture_date};

const MAX_FILES: usize = 20_000;
static FILE_OPERATIONS: std::sync::Mutex<()> = std::sync::Mutex::new(());
const MONTHS: [&str; 12] = [
    "January",
    "February",
    "March",
    "April",
    "May",
    "June",
    "July",
    "August",
    "September",
    "October",
    "November",
    "December",
];

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum GroupBy {
    /// Four folders: Doc, Media, Binary, and Others.
    Simple,
    Type,
    Extension,
    Date,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum DateFolders {
    #[default]
    None,
    Year,
    Month,
    YearMonth,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PlanRequest {
    pub source: String,
    pub group_by: GroupBy,
    #[serde(default)]
    pub date_folders: DateFolders,
    /// Folder that receives the grouped folders; defaults to `source`.
    #[serde(default)]
    pub destination: Option<String>,
    #[serde(default)]
    pub match_extension: Option<String>,
    #[serde(default)]
    pub match_category: Option<String>,
    #[serde(default)]
    pub skip_collisions: bool,
    /// Sort only photos or only videos, by capture date. Requires [`GroupBy::Date`].
    #[serde(default)]
    pub media: Option<MediaKind>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlannedOperation {
    pub id: u32,
    pub source: String,
    pub destination: String,
    pub file_name: String,
    pub destination_folder: String,
    pub size_bytes: u64,
    pub modified: String,
    pub renamed: bool,
    pub identity: Option<FileIdentity>,
    /// Date taken or recorded, `YYYY-MM-DD`, when a media sort read one.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub captured: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SkippedFile {
    pub file_name: String,
    pub path: String,
    pub reason: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Plan {
    pub source: String,
    pub destination: String,
    pub operations: Vec<PlannedOperation>,
    pub skipped: Vec<SkippedFile>,
    pub total_bytes: u64,
    pub folder_count: usize,
    pub renamed_count: usize,
    /// Files a media sort leaves alone because they are another kind of file.
    pub other_files: usize,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ApplyOperation {
    pub id: u32,
    pub source: String,
    pub destination: String,
    pub identity: Option<FileIdentity>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ApplyRequest {
    pub source: String,
    pub destination: String,
    pub title: String,
    pub journal_directory: String,
    pub operations: Vec<ApplyOperation>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Failure {
    pub file_name: String,
    pub reason: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ApplyResult {
    pub batch_id: String,
    pub moved: usize,
    pub failed: usize,
    pub failures: Vec<Failure>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct UndoRequest {
    pub journal_directory: String,
    pub batch_id: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UndoResult {
    pub restored: usize,
    pub conflicts: Vec<Failure>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Locations {
    pub downloads: Option<String>,
    pub desktop: Option<String>,
    pub documents: Option<String>,
    pub pictures: Option<String>,
    pub videos: Option<String>,
}

pub fn locations() -> Locations {
    let text = |f| known_folder(f).map(|p| p.to_string_lossy().into_owned());
    Locations {
        downloads: text(KnownFolder::Downloads),
        desktop: text(KnownFolder::Desktop),
        documents: text(KnownFolder::Documents),
        pictures: text(KnownFolder::Pictures),
        videos: text(KnownFolder::Videos),
    }
}

/// Rejects anything other than an ordinary local folder outside Windows,
/// program, and application-data locations.
pub fn validate_folder(path: &str) -> Result<PathBuf> {
    let folder = PathBuf::from(path.trim());
    if !is_plain_absolute(&folder) {
        return Err(CoreError::invalid("Choose a folder on a local drive."));
    }
    let _guard = DirectoryGuard::parents(&folder).map_err(CoreError::rejected)?;
    let metadata = fs::symlink_metadata(&folder)
        .map_err(|_| CoreError::new(ErrorCode::NotFound, "That folder doesn't exist."))?;
    if !metadata.is_dir() || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
        return Err(CoreError::rejected(
            "Choose a regular folder, not a link to another location.",
        ));
    }
    // Resolve 8.3 aliases before comparing against sensitive/install roots.
    let canonical = fs::canonicalize(&folder)
        .map_err(|_| CoreError::rejected("The folder cannot be resolved safely."))?;
    let text = canonical.to_string_lossy();
    let folder = PathBuf::from(text.strip_prefix(r"\\?\").unwrap_or(&text));
    if is_drive_root(&folder) {
        return Err(CoreError::rejected(
            "Choose a folder rather than a whole drive.",
        ));
    }
    use KnownFolder::*;
    for root in [
        Windows,
        ProgramFiles,
        ProgramFilesX86,
        ProgramData,
        RoamingAppData,
        LocalAppData,
    ]
    .into_iter()
    .filter_map(known_folder)
    {
        if is_within(&folder, &root) {
            return Err(CoreError::rejected(
                "cleanup doesn't organize Windows, program, or app data folders.",
            ));
        }
    }
    if known_folder(Profile).is_some_and(|p| same_path(&folder, &p)) {
        return Err(CoreError::rejected(
            "Choose a folder inside your user folder rather than the user folder itself.",
        ));
    }
    let mut installed_roots = Vec::new();
    cleanup_discovery::scan(
        &cleanup_discovery::ScanOptions {
            include_msix: false,
            batch_size: 200,
        },
        &mut |batch| {
            installed_roots.extend(batch.into_iter().filter_map(|app| app.install_location))
        },
        &|| false,
    );
    if installed_roots
        .iter()
        .map(PathBuf::from)
        .filter(|path| is_plain_absolute(path))
        .any(|path| is_within(&folder, &path))
    {
        return Err(CoreError::rejected(
            "This folder belongs to an installed application. Choose a folder of personal files.",
        ));
    }
    Ok(folder)
}

/// Why a file is left alone, or `None` if it may be organized.
fn skip_reason(metadata: &Metadata, extension: &str) -> Option<&'static str> {
    let attributes = metadata.file_attributes();
    if attributes & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
        Some("Link to another location")
    } else if attributes & (FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM) != 0 {
        Some("Hidden or system file")
    } else if attributes
        & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
            | FILE_ATTRIBUTE_RECALL_ON_OPEN
            | FILE_ATTRIBUTE_OFFLINE)
        != 0
    {
        Some("Online-only cloud file; moving it would download it")
    } else if is_partial_download(extension) {
        Some("Download still in progress")
    } else if is_shortcut(extension) {
        Some("Shortcut")
    } else {
        None
    }
}

/// Local calendar (year, month, day) of a UTC FILETIME tick count.
fn local_date(utc: u64) -> (i64, u32, u32) {
    let input = FILETIME {
        dwLowDateTime: utc as u32,
        dwHighDateTime: (utc >> 32) as u32,
    };
    let mut local = FILETIME {
        dwLowDateTime: 0,
        dwHighDateTime: 0,
    };
    // SAFETY: both structures are valid.
    let ticks = if unsafe { FileTimeToLocalFileTime(&input, &mut local) } != 0 {
        (u64::from(local.dwHighDateTime) << 32) | u64::from(local.dwLowDateTime)
    } else {
        utc
    };
    let seconds = (ticks as i64 - 116_444_736_000_000_000) / 10_000_000;
    civil_from_days(seconds.div_euclid(86_400))
}

fn date_folder(kind: DateFolders, year: i64, month: u32) -> Option<String> {
    let month_name = format!("{month:02} - {}", MONTHS[(month as usize).clamp(1, 12) - 1]);
    match kind {
        DateFolders::None => None,
        DateFolders::Year => Some(year.to_string()),
        DateFolders::Month => Some(month_name),
        DateFolders::YearMonth => Some(format!(r"{year}\{month_name}")),
    }
}

fn relative_folder(request: &PlanRequest, extension: &str, year: i64, month: u32) -> String {
    let dates = date_folder(request.date_folders, year, month);
    match request.group_by {
        GroupBy::Simple => join(simple_group(extension).folder_name().to_owned(), dates),
        GroupBy::Type => join(categorize(extension).folder_name().to_owned(), dates),
        GroupBy::Extension => join(
            if extension.is_empty() {
                "No extension".to_owned()
            } else {
                extension.to_ascii_uppercase()
            },
            dates,
        ),
        GroupBy::Date => date_folder(
            match request.date_folders {
                DateFolders::None => DateFolders::YearMonth,
                other => other,
            },
            year,
            month,
        )
        .unwrap_or_default(),
    }
}

fn join(first: String, second: Option<String>) -> String {
    match second {
        Some(second) => format!(r"{first}\{second}"),
        None => first,
    }
}

/// First free name in `folder`, never reusing an existing file or a name
/// already claimed by an earlier move in the same plan (case-insensitive).
fn unique_destination(
    folder: &Path,
    file_name: &str,
    claimed: &mut HashSet<String>,
) -> (PathBuf, bool) {
    let path = Path::new(file_name);
    let stem = path
        .file_stem()
        .map(|s| s.to_string_lossy().into_owned())
        .unwrap_or_default();
    let extension = path
        .extension()
        .map(|e| format!(".{}", e.to_string_lossy()))
        .unwrap_or_default();
    let mut candidate = folder.join(file_name);
    let mut counter = 1;
    loop {
        let key = candidate.to_string_lossy().to_lowercase();
        if !claimed.contains(&key) && fs::symlink_metadata(&candidate).is_err() {
            claimed.insert(key);
            return (candidate, counter > 1);
        }
        counter += 1;
        candidate = folder.join(format!("{stem} ({counter}){extension}"));
    }
}

pub fn plan(request: &PlanRequest) -> Result<Plan> {
    plan_with_cancel(request, &|| false)
}

pub fn plan_with_cancel(request: &PlanRequest, cancel: &dyn Fn() -> bool) -> Result<Plan> {
    if request.media.is_some() && request.group_by != GroupBy::Date {
        return Err(CoreError::invalid(
            "Photo and video sorting groups files by date.",
        ));
    }
    let source = validate_folder(&request.source)?;
    let root = match request.destination.as_deref().map(str::trim) {
        Some(destination) if !destination.is_empty() => validate_folder(destination)?,
        _ => source.clone(),
    };

    let _source_guard =
        DirectoryGuard::parents(&source.join("probe")).map_err(CoreError::rejected)?;
    let mut entries: Vec<(PathBuf, Metadata)> = Vec::new();
    let mut skipped = Vec::new();
    for (count, entry) in fs::read_dir(&source)
        .map_err(|e| CoreError::win32("Reading the folder", e.raw_os_error().unwrap_or(0) as u32))?
        .enumerate()
    {
        if cancel() {
            return Err(CoreError::new(ErrorCode::Cancelled, "Preview cancelled."));
        }
        if count >= MAX_FILES {
            return Err(CoreError::rejected(
                "This folder has more than 20,000 entries. Choose a smaller folder.",
            ));
        }
        let entry = entry.map_err(|error| {
            CoreError::win32(
                "Reading a folder entry",
                error.raw_os_error().unwrap_or(5) as u32,
            )
        })?;
        let path = entry.path();
        match fs::symlink_metadata(&path) {
            Ok(metadata) => entries.push((path, metadata)),
            Err(error) => skipped.push(SkippedFile {
                file_name: entry.file_name().to_string_lossy().into_owned(),
                path: path.to_string_lossy().into_owned(),
                reason: describe_file_error(error.raw_os_error().unwrap_or(5) as u32),
            }),
        }
    }
    entries.sort_by_key(|(path, _)| path.file_name().map(|n| n.to_string_lossy().to_lowercase()));

    let mut claimed = HashSet::new();
    let mut folders = HashSet::new();
    let mut operations = Vec::new();
    let mut total_bytes = 0u64;
    let mut other_files = 0;
    for (path, metadata) in entries.into_iter().take(MAX_FILES) {
        if cancel() {
            return Err(CoreError::new(ErrorCode::Cancelled, "Preview cancelled."));
        }
        let is_link = metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0;
        if metadata.is_dir() && !is_link {
            continue;
        }
        let file_name = path
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_default();
        let extension = path
            .extension()
            .map(|e| e.to_string_lossy().to_lowercase())
            .unwrap_or_default();
        if let Some(reason) = skip_reason(&metadata, &extension) {
            skipped.push(SkippedFile {
                file_name,
                path: path.to_string_lossy().into_owned(),
                reason: reason.to_owned(),
            });
            continue;
        }
        if request
            .media
            .is_some_and(|media| !media.includes(&extension))
        {
            other_files += 1;
            continue;
        }
        let Ok(mut file) = fs::OpenOptions::new().read(true).write(true).open(&path) else {
            skipped.push(SkippedFile {
                file_name,
                path: path.to_string_lossy().into_owned(),
                reason: "File is in use or access is restricted".into(),
            });
            continue;
        };
        if request.match_extension.as_ref().is_some_and(|wanted| {
            !extension.eq_ignore_ascii_case(wanted.trim().trim_start_matches('.'))
        }) || request.match_category.as_ref().is_some_and(|wanted| {
            let group = if request.group_by == GroupBy::Simple {
                simple_group(&extension).folder_name()
            } else {
                categorize(&extension).folder_name()
            };
            !group.eq_ignore_ascii_case(wanted)
        }) {
            skipped.push(SkippedFile {
                file_name,
                path: path.to_string_lossy().into_owned(),
                reason: "Does not match the rule".into(),
            });
            continue;
        }
        // Reading the metadata does not change the identity checked at apply.
        let captured = request
            .media
            .and_then(|_| capture_date(&mut file, &extension, local_date));
        drop(file);
        let changed = local_date(metadata.last_write_time());
        let (year, month) = captured.map_or((changed.0, changed.1), |date| (date.year, date.month));
        let folder = relative_folder(request, &extension, year, month);
        if DirectoryGuard::parents(&root.join(&folder).join(&file_name)).is_err() {
            skipped.push(SkippedFile {
                file_name,
                path: path.to_string_lossy().into_owned(),
                reason: "Destination has an inaccessible folder or a link".into(),
            });
            continue;
        }
        if request.skip_collisions
            && fs::symlink_metadata(root.join(&folder).join(&file_name)).is_ok()
        {
            skipped.push(SkippedFile {
                file_name,
                path: path.to_string_lossy().into_owned(),
                reason: "Destination name is already in use".into(),
            });
            continue;
        }
        let (destination, renamed) =
            unique_destination(&root.join(&folder), &file_name, &mut claimed);
        folders.insert(folder.to_lowercase());
        total_bytes += metadata.len();
        operations.push(PlannedOperation {
            id: operations.len() as u32 + 1,
            source: path.to_string_lossy().into_owned(),
            destination: destination.to_string_lossy().into_owned(),
            file_name,
            destination_folder: folder,
            size_bytes: metadata.len(),
            // The local date, matching the date folders built from it.
            modified: if metadata.last_write_time() > 0 {
                format!("{:04}-{:02}-{:02}", changed.0, changed.1, changed.2)
            } else {
                "Not reported".into()
            },
            renamed,
            identity: FileIdentity::of(&path),
            captured: captured.map(|date| date.iso()),
        });
    }
    let renamed_count = operations.iter().filter(|o| o.renamed).count();
    // Leave room for the request envelope and the managed JSON encoder's
    // Unicode/HTML escaping. Ordinary ASCII paths retain the 20,000-file limit.
    let mut request_bytes = 128 * 1024;
    for operation in &operations {
        let apply = ApplyOperation {
            id: operation.id,
            source: operation.source.clone(),
            destination: operation.destination.clone(),
            identity: operation.identity,
        };
        request_bytes += serde_json::to_vec(&apply)
            .map_err(|e| CoreError::invalid(e.to_string()))?
            .len();
        request_bytes += [&apply.source, &apply.destination]
            .iter()
            .flat_map(|text| text.bytes())
            .filter(|byte| !byte.is_ascii_alphanumeric() && !b"_-. :/\\".contains(byte))
            .count()
            * 5;
    }
    if request_bytes > 16 * 1024 * 1024 {
        return Err(CoreError::rejected(
            "The preview is too large to apply safely. Choose a smaller folder or filter by file type.",
        ));
    }
    Ok(Plan {
        source: source.to_string_lossy().into_owned(),
        destination: root.to_string_lossy().into_owned(),
        operations,
        skipped,
        total_bytes,
        folder_count: folders.len(),
        renamed_count,
        other_files,
    })
}

/// Creates the missing folders between `root` and `folder`, recording each
/// one so undo can remove it again if it ends up empty.
fn create_folders(folder: &Path, root: &Path, journal: &Journal, batch_id: &str) -> Result<()> {
    let mut missing = Vec::new();
    let mut current = folder.to_path_buf();
    while !current.exists() {
        if !is_within(&current, root) || same_path(&current, root) {
            return Err(CoreError::rejected(
                "Destination is outside the chosen folder.",
            ));
        }
        missing.push(current.clone());
        if !current.pop() {
            break;
        }
    }
    for directory in missing.into_iter().rev() {
        fs::create_dir(&directory).map_err(|e| {
            CoreError::win32("Creating a folder", e.raw_os_error().unwrap_or(0) as u32)
        })?;
        journal.append(&Event::DirectoryCreated {
            batch_id: batch_id.to_owned(),
            path: directory.to_string_lossy().into_owned(),
        })?;
    }
    Ok(())
}

pub fn apply(request: &ApplyRequest) -> Result<ApplyResult> {
    apply_with_cancel(request, &|| false)
}

pub fn apply_with_cancel(request: &ApplyRequest, cancel: &dyn Fn() -> bool) -> Result<ApplyResult> {
    let _operation = FILE_OPERATIONS
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let source = validate_folder(&request.source)?;
    let root = validate_folder(&request.destination)?;
    let journal = Journal::open(&request.journal_directory)?;
    let _source_guard =
        DirectoryGuard::parents(&source.join("probe")).map_err(CoreError::rejected)?;
    let _root_guard = DirectoryGuard::parents(&root.join("probe")).map_err(CoreError::rejected)?;

    // Validate everything before recording or moving anything.
    let mut planned = Vec::with_capacity(request.operations.len());
    let mut ids = HashSet::new();
    let mut sources = HashSet::new();
    for op in &request.operations {
        if !ids.insert(op.id) || !sources.insert(op.source.to_lowercase()) {
            return Err(CoreError::rejected(
                "Duplicate file operations were rejected.",
            ));
        }
        let from = PathBuf::from(&op.source);
        let to = PathBuf::from(&op.destination);
        let direct_child = from
            .parent()
            .is_some_and(|parent| same_path(parent, &source));
        if !is_plain_absolute(&from)
            || !is_plain_absolute(&to)
            || !direct_child
            || !is_within(&to, &root)
            || same_path(&to, &root)
        {
            return Err(CoreError::rejected(format!(
                "The move for {:?} doesn't match the previewed folder; nothing was changed.",
                from.file_name().unwrap_or_default()
            )));
        }
        planned.push(PlannedMove {
            op_id: op.id,
            source: op.source.clone(),
            destination: op.destination.clone(),
        });
    }

    let batch_id = new_batch_id();
    journal.append(&Event::BatchStarted {
        batch_id: batch_id.clone(),
        kind: BatchKind::Organize,
        title: request.title.clone(),
        at_unix_ms: cleanup_core::time::unix_millis_now(),
        operations: planned.clone(),
    })?;

    let mut moved = 0;
    let mut failures = Vec::new();
    for (op, approved) in planned.iter().zip(&request.operations) {
        if cancel() {
            break;
        }
        let from = PathBuf::from(&op.source);
        let file_name = from
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_default();
        let outcome = (|| -> std::result::Result<(PathBuf, FileIdentity), String> {
            let file = LockedFile::open(&from).map_err(|error| error.message)?;
            if approved.identity.is_none() || file.identity().ok() != approved.identity {
                return Err("The file changed since the preview. Preview again.".into());
            }
            let metadata = file.metadata().map_err(|_| describe_file_error(2))?;
            let extension = from
                .extension()
                .map(|e| e.to_string_lossy().to_lowercase())
                .unwrap_or_default();
            if !metadata.is_file() || skip_reason(&metadata, &extension).is_some() {
                return Err("The file changed since the preview.".into());
            }
            let wanted = PathBuf::from(&op.destination);
            let folder = wanted.parent().ok_or("Invalid destination.")?.to_path_buf();
            let _destination_guard = DirectoryGuard::parents(&wanted)?;
            create_folders(&folder, &root, &journal, &batch_id).map_err(|e| e.message)?;
            let _created_guard = DirectoryGuard::parents(&wanted)?;
            // Keep the exact approved destination; a new collision fails.
            let destination = wanted;
            let identity = file
                .move_to(&destination, |identity| {
                    journal.append(&Event::MovePrepared {
                        batch_id: batch_id.clone(),
                        op_id: op.op_id,
                        identity,
                    })
                })
                .map_err(|error| error.message)?;
            Ok((destination, identity))
        })();
        let (outcome, destination, detail, identity) = match outcome {
            Ok((destination, identity)) => {
                moved += 1;
                (
                    MoveOutcome::Moved,
                    destination.to_string_lossy().into_owned(),
                    None,
                    Some(identity),
                )
            }
            Err(reason) => {
                failures.push(Failure {
                    file_name: file_name.clone(),
                    reason: reason.clone(),
                });
                (
                    MoveOutcome::Failed,
                    op.destination.clone(),
                    Some(reason),
                    None,
                )
            }
        };
        journal.append(&Event::MoveCompleted {
            batch_id: batch_id.clone(),
            op_id: op.op_id,
            outcome,
            destination,
            detail,
            identity,
        })?;
    }
    journal.append(&Event::BatchFinished {
        batch_id: batch_id.clone(),
        at_unix_ms: cleanup_core::time::unix_millis_now(),
    })?;
    Ok(ApplyResult {
        batch_id,
        moved,
        failed: failures.len(),
        failures,
    })
}

pub fn undo(request: &UndoRequest) -> Result<UndoResult> {
    let _operation = FILE_OPERATIONS
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let journal = Journal::open(&request.journal_directory)?;
    let events = journal.read_batch(&request.batch_id)?;
    let batch = organize_batch(&events, &request.batch_id).ok_or_else(|| {
        CoreError::new(
            ErrorCode::NotFound,
            "There is nothing to undo for this entry.",
        )
    })?;

    let mut restored = 0;
    let mut conflicts = Vec::new();
    for state in batch.moves.iter().filter(|m| m.moved && !m.undone).rev() {
        let original = PathBuf::from(&state.source);
        let current = PathBuf::from(&state.destination);
        let file_name = original
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_default();
        let original_guard = DirectoryGuard::parents(&original);
        let current_guard = DirectoryGuard::parents(&current);
        let file_guard = LockedFile::open(&current);
        let result: std::result::Result<(), String> =
            if original_guard.is_err() || current_guard.is_err() {
                Err("A parent folder was replaced by a link or cannot be inspected.".into())
            } else if file_guard.is_err() {
                Err("The file is in use or cannot be opened safely.".into())
            } else if !is_plain_absolute(&original) || !is_plain_absolute(&current) {
                Err("The history entry is not valid.".into())
            } else if file_guard
                .as_ref()
                .ok()
                .and_then(|file| file.identity().ok())
                != state.identity
            {
                Err("The file was changed after it was organized.".into())
            } else if fs::symlink_metadata(&original).is_ok() {
                Err("Another file now uses the original name.".into())
            } else if !original.parent().is_some_and(Path::is_dir) {
                Err("The original folder no longer exists.".into())
            } else {
                file_guard
                    .unwrap()
                    .move_to(&original, |identity| {
                        journal.append(&Event::UndoPrepared {
                            batch_id: request.batch_id.clone(),
                            op_id: state.op_id,
                            identity,
                        })
                    })
                    .map(|_| ())
                    .map_err(|error| error.message)
            };
        let (outcome, detail) = match result {
            Ok(()) => {
                restored += 1;
                (UndoOutcome::Restored, None)
            }
            Err(reason) => {
                conflicts.push(Failure {
                    file_name,
                    reason: reason.clone(),
                });
                (UndoOutcome::Conflict, Some(reason))
            }
        };
        journal.append(&Event::MoveUndone {
            batch_id: request.batch_id.clone(),
            op_id: state.op_id,
            outcome,
            detail,
        })?;
    }
    // Remove folders this batch created, deepest first, only if now empty.
    for directory in batch.created_directories.iter().rev() {
        let _ = fs::remove_dir(directory);
    }
    Ok(UndoResult {
        restored,
        conflicts,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Fixture {
        root: PathBuf,
        inbox: PathBuf,
        journal: PathBuf,
    }

    impl Fixture {
        fn new(name: &str) -> Self {
            // The system temp folder lives under LocalAppData, which the
            // organizer refuses, so tests work in a folder beside the target dir.
            let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("..\\..\\target\\organizer-tests")
                .join(format!("{name}-{}", new_batch_id()));
            let root = std::path::absolute(&root).unwrap();
            let root = PathBuf::from(root.to_string_lossy().replace(r"\\?\", ""));
            let root: PathBuf = root.components().collect();
            let inbox = root.join("inbox");
            fs::create_dir_all(&inbox).unwrap();
            Self {
                journal: root.join("journal"),
                inbox,
                root,
            }
        }

        fn file(&self, name: &str, content: &str) -> PathBuf {
            let path = self.inbox.join(name);
            fs::write(&path, content).unwrap();
            path
        }

        fn plan(&self, group_by: GroupBy, date_folders: DateFolders) -> Plan {
            self.request(group_by, date_folders, None).unwrap()
        }

        fn request(
            &self,
            group_by: GroupBy,
            date_folders: DateFolders,
            media: Option<MediaKind>,
        ) -> Result<Plan> {
            plan(&PlanRequest {
                source: self.inbox.to_string_lossy().into_owned(),
                group_by,
                date_folders,
                destination: None,
                match_extension: None,
                match_category: None,
                skip_collisions: false,
                media,
            })
        }

        fn bytes(&self, name: &str, content: &[u8]) -> PathBuf {
            let path = self.inbox.join(name);
            fs::write(&path, content).unwrap();
            path
        }

        fn apply(&self, plan: &Plan) -> ApplyResult {
            apply(&ApplyRequest {
                source: plan.source.clone(),
                destination: plan.destination.clone(),
                title: "test".into(),
                journal_directory: self.journal.to_string_lossy().into_owned(),
                operations: plan
                    .operations
                    .iter()
                    .map(|o| ApplyOperation {
                        id: o.id,
                        source: o.source.clone(),
                        destination: o.destination.clone(),
                        identity: o.identity,
                    })
                    .collect(),
            })
            .unwrap()
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.root);
        }
    }

    #[test]
    fn interrupted_move_and_undo_are_reconciled_by_prepared_identity() {
        let f = Fixture::new("recovery");
        f.file("a.png", "original");
        let plan = f.plan(GroupBy::Type, DateFolders::None);
        let operation = &plan.operations[0];
        fs::create_dir_all(Path::new(&operation.destination).parent().unwrap()).unwrap();
        let journal = Journal::open(f.journal.to_str().unwrap()).unwrap();
        let id = new_batch_id();
        journal
            .append(&Event::BatchStarted {
                batch_id: id.clone(),
                kind: BatchKind::Organize,
                title: "interrupted fixture".into(),
                at_unix_ms: 1,
                operations: vec![PlannedMove {
                    op_id: operation.id,
                    source: operation.source.clone(),
                    destination: operation.destination.clone(),
                }],
            })
            .unwrap();
        LockedFile::open(Path::new(&operation.source))
            .unwrap()
            .move_to(Path::new(&operation.destination), |identity| {
                journal.append(&Event::MovePrepared {
                    batch_id: id.clone(),
                    op_id: operation.id,
                    identity,
                })
            })
            .unwrap();
        let batch = organize_batch(&journal.read_batch(&id).unwrap(), &id).unwrap();
        assert!(batch.moves[0].moved);
        LockedFile::open(Path::new(&operation.destination))
            .unwrap()
            .move_to(Path::new(&operation.source), |identity| {
                journal.append(&Event::UndoPrepared {
                    batch_id: id.clone(),
                    op_id: operation.id,
                    identity,
                })
            })
            .unwrap();
        let batch = organize_batch(&journal.read_batch(&id).unwrap(), &id).unwrap();
        // Recovery must remember the completed forward move even after a crash
        // during undo, when the destination has already disappeared.
        assert!(batch.moves[0].undone);
        assert_eq!(fs::read_to_string(&operation.source).unwrap(), "original");
    }

    #[test]
    fn simple_mode_sorts_into_doc_media_binary_and_others() {
        let f = Fixture::new("simple");
        f.file("a.pdf", "1");
        f.file("b.mp3", "2");
        f.file("c.dll", "3");
        f.file("d.zip", "4");
        f.file("e.blend", "5");
        let plan = f.plan(GroupBy::Simple, DateFolders::None);
        let folders: Vec<&str> = plan
            .operations
            .iter()
            .map(|o| o.destination_folder.as_str())
            .collect();
        assert_eq!(folders, vec!["Doc", "Media", "Binary", "Binary", "Others"]);
        let result = f.apply(&plan);
        assert_eq!(result.moved, 5);
        assert!(f.inbox.join("Binary").join("c.dll").exists());
    }

    #[test]
    fn plans_by_type_without_touching_disk() {
        let f = Fixture::new("plan");
        f.file("photo.png", "p");
        f.file("report.pdf", "d");
        f.file("setup.exe", "e");
        f.file("song.crdownload", "partial");
        f.file("shortcut.lnk", "l");
        fs::create_dir(f.inbox.join("Existing folder")).unwrap();
        let plan = f.plan(GroupBy::Type, DateFolders::None);
        let folders: Vec<&str> = plan
            .operations
            .iter()
            .map(|o| o.destination_folder.as_str())
            .collect();
        assert_eq!(folders, vec!["Images", "Documents", "Installers"]);
        let reasons: Vec<&str> = plan.skipped.iter().map(|s| s.reason.as_str()).collect();
        assert_eq!(reasons, vec!["Shortcut", "Download still in progress"]);
        assert!(
            !f.inbox.join("Images").exists(),
            "planning must not create folders"
        );
    }

    #[test]
    fn collisions_are_renamed_never_overwritten() {
        let f = Fixture::new("collide");
        f.file("a.png", "new");
        fs::create_dir(f.inbox.join("Images")).unwrap();
        fs::write(f.inbox.join("Images").join("a.png"), "existing").unwrap();
        let plan = f.plan(GroupBy::Type, DateFolders::None);
        assert!(plan.operations[0].destination.ends_with("a (2).png"));
        assert!(plan.operations[0].renamed);
        let applied = f.apply(&plan);
        assert_eq!(applied.moved, 1, "{applied:?}");
        assert_eq!(
            fs::read_to_string(f.inbox.join("Images").join("a.png")).unwrap(),
            "existing"
        );
        assert_eq!(
            fs::read_to_string(f.inbox.join("Images").join("a (2).png")).unwrap_or_else(|error| {
                panic!(
                    "Missing approved destination: {error}; planned={:?}; actual={:?}",
                    plan.operations[0].destination,
                    fs::read_dir(f.inbox.join("Images"))
                        .unwrap()
                        .map(|entry| entry.unwrap().file_name())
                        .collect::<Vec<_>>()
                )
            }),
            "new"
        );
    }

    #[test]
    fn case_only_conflicts_within_a_plan_are_resolved() {
        let mut claimed = HashSet::new();
        let folder = Path::new(r"C:\nonexistent-cleanup-test");
        let (first, _) = unique_destination(folder, "Report.PDF", &mut claimed);
        let (second, renamed) = unique_destination(folder, "report.pdf", &mut claimed);
        assert_ne!(
            first.to_string_lossy().to_lowercase(),
            second.to_string_lossy().to_lowercase()
        );
        assert!(renamed);
    }

    #[test]
    fn apply_then_undo_restores_files_and_removes_created_folders() {
        let f = Fixture::new("undo");
        f.file("a.png", "1");
        f.file("b.txt", "2");
        f.file("ünïcødé 日本.zip", "3");
        let plan = f.plan(GroupBy::Type, DateFolders::Year);
        let result = f.apply(&plan);
        assert_eq!((result.moved, result.failed), (3, 0));
        assert!(!f.inbox.join("a.png").exists());

        let undo_result = undo(&UndoRequest {
            journal_directory: f.journal.to_string_lossy().into_owned(),
            batch_id: result.batch_id.clone(),
        })
        .unwrap();
        assert_eq!(undo_result.restored, 3);
        assert!(f.inbox.join("a.png").exists() && f.inbox.join("ünïcødé 日本.zip").exists());
        assert!(
            !f.inbox.join("Images").exists(),
            "created folders are removed when empty"
        );
    }

    #[test]
    fn undo_refuses_changed_files_and_taken_names() {
        let f = Fixture::new("conflict");
        f.file("a.png", "1");
        f.file("b.png", "2");
        let plan = f.plan(GroupBy::Type, DateFolders::None);
        let result = f.apply(&plan);
        // a.png is modified after organizing; a new b.png appears at the original location.
        fs::write(f.inbox.join("Images").join("a.png"), "changed!").unwrap();
        fs::write(f.inbox.join("b.png"), "new file").unwrap();
        let undo_result = undo(&UndoRequest {
            journal_directory: f.journal.to_string_lossy().into_owned(),
            batch_id: result.batch_id,
        })
        .unwrap();
        assert_eq!(undo_result.restored, 0);
        assert_eq!(undo_result.conflicts.len(), 2);
        assert_eq!(
            fs::read_to_string(f.inbox.join("b.png")).unwrap(),
            "new file"
        );
    }

    #[test]
    fn tampered_apply_requests_are_rejected_before_anything_moves() {
        let f = Fixture::new("tamper");
        let file = f.file("a.png", "1");
        let request = ApplyRequest {
            source: f.inbox.to_string_lossy().into_owned(),
            destination: f.inbox.to_string_lossy().into_owned(),
            title: "t".into(),
            journal_directory: f.journal.to_string_lossy().into_owned(),
            operations: vec![ApplyOperation {
                id: 1,
                source: file.to_string_lossy().into_owned(),
                destination: f.root.join("outside.png").to_string_lossy().into_owned(),
                identity: FileIdentity::of(&file),
            }],
        };
        assert!(apply(&request).is_err());
        assert!(file.exists());
    }

    #[test]
    fn system_and_app_data_folders_are_refused() {
        for folder in [
            known_folder(KnownFolder::Windows).unwrap(),
            known_folder(KnownFolder::ProgramFiles).unwrap(),
            known_folder(KnownFolder::LocalAppData).unwrap(),
            known_folder(KnownFolder::Profile).unwrap(),
            PathBuf::from(r"C:\"),
        ] {
            assert!(
                validate_folder(&folder.to_string_lossy()).is_err(),
                "{folder:?}"
            );
        }
        assert!(validate_folder("relative").is_err());
    }

    #[test]
    fn date_grouping_builds_year_and_month_folders() {
        assert_eq!(
            date_folder(DateFolders::YearMonth, 2024, 3).as_deref(),
            Some(r"2024\03 - March")
        );
        assert_eq!(
            date_folder(DateFolders::Month, 2024, 12).as_deref(),
            Some("12 - December")
        );
        assert_eq!(
            date_folder(DateFolders::Year, 2024, 1).as_deref(),
            Some("2024")
        );
    }

    #[test]
    fn new_collisions_and_changed_sources_require_a_new_preview() {
        let f = Fixture::new("stale-preview");
        f.file("photo.png", "original");
        let preview = f.plan(GroupBy::Type, DateFolders::None);
        fs::create_dir_all(f.inbox.join("Images")).unwrap();
        fs::write(f.inbox.join("Images\\photo.png"), "new occupant").unwrap();
        let result = f.apply(&preview);
        assert_eq!((result.moved, result.failed), (0, 1));
        assert_eq!(
            fs::read_to_string(f.inbox.join("Images\\photo.png")).unwrap(),
            "new occupant"
        );
        let preview = f.plan(GroupBy::Type, DateFolders::None);
        f.file("photo.png", "changed since preview");
        assert_eq!(f.apply(&preview).moved, 0);
    }

    #[test]
    fn cancelled_apply_leaves_files_untouched() {
        let f = Fixture::new("cancel");
        let file = f.file("photo.png", "image");
        let plan = f.plan(GroupBy::Type, DateFolders::None);
        let request = ApplyRequest {
            source: plan.source,
            destination: plan.destination,
            title: "cancel test".into(),
            journal_directory: f.journal.to_string_lossy().into_owned(),
            operations: plan
                .operations
                .into_iter()
                .map(|o| ApplyOperation {
                    id: o.id,
                    source: o.source,
                    destination: o.destination,
                    identity: o.identity,
                })
                .collect(),
        };
        assert_eq!(apply_with_cancel(&request, &|| true).unwrap().moved, 0);
        assert!(file.exists());
    }

    #[test]
    fn cancelled_preview_stops_before_producing_operations() {
        let f = Fixture::new("cancel-preview");
        f.file("photo.png", "image");
        let request = PlanRequest {
            source: f.inbox.to_string_lossy().into_owned(),
            group_by: GroupBy::Type,
            date_folders: DateFolders::None,
            destination: None,
            match_extension: None,
            match_category: None,
            skip_collisions: false,
            media: None,
        };
        assert_eq!(
            plan_with_cancel(&request, &|| true).unwrap_err().code,
            ErrorCode::Cancelled
        );
        assert!(!f.inbox.join("Images").exists());
    }

    #[test]
    fn photos_sort_by_date_taken_and_leave_other_files_alone() {
        use crate::media::tests::{exif, jpeg_with, mp4_with};
        let f = Fixture::new("photos");
        f.bytes(
            "IMG_0001.jpg",
            &jpeg_with(&exif(false, 0x9003, "2019:07:15 10:30:00")),
        );
        f.file("screenshot.png", "no metadata");
        f.file("notes.txt", "not a photo");
        f.bytes("clip.mp4", &mp4_with(1_563_192_000, 0));
        let plan = f
            .request(
                GroupBy::Date,
                DateFolders::YearMonth,
                Some(MediaKind::Photos),
            )
            .unwrap();
        assert_eq!(plan.other_files, 2, "the text file and the video stay");
        assert!(plan.skipped.is_empty());
        let names: Vec<&str> = plan
            .operations
            .iter()
            .map(|o| o.file_name.as_str())
            .collect();
        assert_eq!(names, vec!["IMG_0001.jpg", "screenshot.png"]);
        let taken = &plan.operations[0];
        assert_eq!(taken.destination_folder, r"2019\07 - July");
        assert_eq!(taken.captured.as_deref(), Some("2019-07-15"));
        // Without a date taken, the local last-write date decides the folder,
        // and the preview shows that same date.
        let fallback = &plan.operations[1];
        assert_eq!(fallback.captured, None);
        let changed = &fallback.modified;
        assert!(
            fallback.destination_folder.starts_with(&format!(
                r"{}\{} - ",
                &changed[..4],
                &changed[5..7]
            )),
            "{} from {changed}",
            fallback.destination_folder
        );

        let result = f.apply(&plan);
        assert_eq!((result.moved, result.failed), (2, 0));
        assert!(f.inbox.join(r"2019\07 - July\IMG_0001.jpg").exists());
        assert!(f.inbox.join("notes.txt").exists() && f.inbox.join("clip.mp4").exists());
        let undone = undo(&UndoRequest {
            journal_directory: f.journal.to_string_lossy().into_owned(),
            batch_id: result.batch_id,
        })
        .unwrap();
        assert_eq!(undone.restored, 2);
        assert!(f.inbox.join("IMG_0001.jpg").exists() && !f.inbox.join("2019").exists());
    }

    #[test]
    fn videos_sort_by_recording_date() {
        use crate::media::tests::mp4_with;
        let f = Fixture::new("videos");
        f.bytes("clip.mp4", &mp4_with(1_563_192_000, 1));
        f.file("photo.jpg", "not a video");
        let plan = f
            .request(GroupBy::Date, DateFolders::Year, Some(MediaKind::Videos))
            .unwrap();
        assert_eq!((plan.operations.len(), plan.other_files), (1, 1));
        assert_eq!(plan.operations[0].destination_folder, "2019");
        assert_eq!(plan.operations[0].captured.as_deref(), Some("2019-07-15"));
    }

    #[test]
    fn media_sorting_requires_date_grouping() {
        let f = Fixture::new("media-group");
        let error = f
            .request(GroupBy::Type, DateFolders::None, Some(MediaKind::Photos))
            .unwrap_err();
        assert_eq!(error.code, ErrorCode::InvalidArgument);
        // Ordinary plans never report capture dates.
        f.bytes(
            "IMG_0001.jpg",
            &crate::media::tests::jpeg_with(&crate::media::tests::exif(
                true,
                0x9003,
                "2019:07:15 10:30:00",
            )),
        );
        let plan = f.plan(GroupBy::Date, DateFolders::YearMonth);
        assert_eq!(plan.operations[0].captured, None);
        assert_eq!(plan.other_files, 0);
    }

    #[test]
    fn destination_links_are_never_followed() {
        let f = Fixture::new("linked-destination");
        f.file("photo.png", "image");
        let outside = f.root.join("outside");
        fs::create_dir(&outside).unwrap();
        std::os::windows::fs::symlink_dir(&outside, f.inbox.join("Images")).unwrap();
        let preview = f.plan(GroupBy::Type, DateFolders::None);
        assert!(preview.operations.is_empty());
        assert!(!outside.join("photo.png").exists());
    }
}
