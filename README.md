# BackupCron for Windows

A free, open-source Windows application for scheduled backups of files,
folders, projects and databases. No purchases, subscriptions or job limits.

## Download

[Download the Windows installer](https://github.com/backupcron/backupcron-windows/releases/download/v1.0.0/BackupCron-1.0.0-win-x64-setup.exe)

Windows 10 version 2004 or later, or Windows 11; 64-bit Intel/AMD.
The installer includes the .NET runtime and installs for the current user
without administrator privileges. The installer is currently unsigned by a
publisher certificate. Verify its SHA-256 against the release page.

## Features

- Individual files, folders and projects: ordinary copies or ZIP archives.
- Project exclusions, including an explicit switch for Git metadata.
- SQLite consistent snapshots, including committed WAL data.
- PostgreSQL and MySQL dumps with their native command-line clients.
- Direct, Docker and SSH database connections and connection checks.
- Table sizes, search, numeric size sorting and complete table exclusions.
- Dependency checks for foreign keys, views, triggers and PostgreSQL partitions.
- Disk space estimates before database backups.
- Manual, interval, hourly, daily and weekly schedules.
- Output templates: `{job}`, `{type}`, `{date}` and `{time}`.
- Retention of the last N successful outputs of the same job and destination.
- Progress, live logs, history, cancellation and verified output publication.
- Passwords encrypted with Windows DPAPI for the current user.
- Launch at login, notification-area operation, notifications and optional sounds.
- Ten interface languages, selected automatically or in settings.

Scheduling works while BackupCron is running. Closing its window keeps it in
the notification area; use the tray menu to quit. Autostart is optional.

SQLite support is included. Direct PostgreSQL/MySQL backups require their
clients; select `pg_dump.exe` or `mysqldump.exe` if automatic discovery fails.
Docker connections require Docker. SSH requires OpenSSH, a configured key and
a trusted host key; database credentials come from `.pgpass` or `.my.cnf` on
the remote server. BackupCron does not bypass SSH host verification.

Windows may require Developer Mode or suitable privileges to recreate symbolic
links in ordinary folder copies. ZIP entries retain links without following
them; use an extraction tool that understands symbolic-link entries.

## Build and verify

Install the .NET 10 SDK. On Windows, install Inno Setup 6 to build the installer.

```powershell
dotnet build BackupCron.Windows.slnx -c Release
dotnet run --project tests/BackupCron.Tests -c Release
./scripts/build-windows.ps1
```

The installer is written to `artifacts/`. GitHub Actions also verifies a real
installation, starts the installed app with isolated fixtures, checks DPAPI
and file/SQLite backups, renders the WPF window, and tests uninstallation.
An additional job restores actual PostgreSQL and MySQL dumps into disposable
databases. Release publication requires both jobs to pass.

The core scenarios can also run on macOS/Linux; the WPF interface runs on Windows.
See [feature parity](docs/feature-parity.md) for the behavior shared with macOS.

## Contributing and support

Bug reports should include the Windows version, backup type, connection mode,
reproduction steps and redacted logs. Do not upload credentials or private data.
Stars, useful issues and pull requests are welcome.

The macOS application is maintained in [backupcron/backupcron](https://github.com/backupcron/backupcron).

## License

MIT. See [LICENSE](LICENSE). The Windows implementation ports behavior from the
MIT-licensed macOS BackupCron and reuses its ten localization dictionaries.
