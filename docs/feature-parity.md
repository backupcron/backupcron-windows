# Behavior shared with macOS BackupCron

| Area | Windows implementation and verification |
| --- | --- |
| Files and folders | Ordinary copies and ZIP; exact byte checks; empty directories; existing outputs are never overwritten. |
| Projects | Exclusion names match at every depth; Git inclusion and the exclusion list represent one setting. |
| SQLite | SQLite online backup API, cancellation between page batches, WAL snapshots, integrity checks and optional complete table removal. |
| SQLite partial copies | Read-only dependency inspection; views/triggers/foreign keys and supported virtual-table dependencies; retained row data and row IDs checked after compaction; source untouched. |
| PostgreSQL/MySQL | Native dumps, connection checks, table sizes and whole-table exclusions; actual fixture dumps restored in CI. |
| Connection modes | Direct executable discovery/custom path; Docker exec; SSH with key authentication and strict host verification. |
| Schedules | Manual, 5–1440 minute intervals, hourly, daily and weekly; no repeated daily run during a DST overlap. |
| Retention | Prune only successful outputs belonging to the same job and current destination, after publication and persisted history. |
| Credentials | Windows DPAPI replaces macOS Keychain; credentials are absent from state.json and command arguments. |
| Feedback | History, live progress/logs, notification-area notifications, optional bounded-volume tones. |
| Background operation | Scheduling continues while the app is running in the notification area. No background service runs after quit. |
| Distribution | Self-contained x64 application, per-user Inno Setup installer, installation/startup/uninstall verification. |

The Windows UI is native WPF. Its layout follows the source/destination/schedule
and table-selection workflows rather than copying macOS window chrome.

Database space estimates are conservative, not exact predictions of SQL/ZIP
size. Unknown capacity is shown as unknown. Complete server backups remain
available when catalog permissions are insufficient; partial backups require
a catalog whose dependencies can be verified.

SQLite extension-defined functions or unsupported virtual-table modules can
prevent a verified partial copy. Include all objects or exclude the uncertain
object as well; the app never edits the source to work around such errors.

Uninstallation preserves job settings, encrypted credentials and backup files.
DPAPI credentials are tied to the Windows user/profile and are not a portable
export format.
