namespace Hermes.Core

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks

/// Platform-specific service registration: Windows Task Scheduler and macOS launchd.
/// Parameterised over FileSystem and Logger algebras for testability.
[<RequireQualifiedAccess>]
module ServiceInstaller =

    /// Result of a service management operation.
    type ServiceResult =
        | Installed
        | Uninstalled
        | Started
        | Stopped
        | AlreadyInstalled
        | AlreadyRunning
        | NotInstalled
        | NotRunning
        | StatusInfo of string
        | Failed of string

    // ─── Platform detection ─────────────────────────────────────────

    type Platform =
        | Windows
        | MacOS
        | Unsupported

    let detectPlatform () : Platform =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then Windows
        elif RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then MacOS
        else Unsupported

    // ─── Executable path ────────────────────────────────────────────

    let private getExePath () : string =
        let asm = System.Reflection.Assembly.GetEntryAssembly()

        match asm with
        | null -> "hermes"
        | a ->
            let loc = a.Location

            if String.IsNullOrEmpty(loc) then
                "hermes"
            elif loc.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then
                let dir = Path.GetDirectoryName(loc)

                match dir with
                | null -> "hermes"
                | d ->
                    let exeName =
                        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then "hermes.exe"
                        else "hermes"

                    Path.Combine(d, exeName)
            else
                loc

    // ─── Windows: Task Scheduler ────────────────────────────────────

    let private windowsTaskName = "HermesDocumentService"

    let private runProcess (logger: Algebra.Logger) (fileName: string) (args: string) : Task<Result<string, string>> =
        task {
            try
                let psi = Diagnostics.ProcessStartInfo(fileName, args)
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true

                use proc = new Diagnostics.Process()
                proc.StartInfo <- psi

                let started = proc.Start()

                if not started then
                    return Error "Failed to start process"
                else
                    let! stdout = proc.StandardOutput.ReadToEndAsync()
                    let! stderr = proc.StandardError.ReadToEndAsync()
                    do! proc.WaitForExitAsync()

                    if proc.ExitCode = 0 then
                        return Ok stdout
                    else
                        let msg =
                            if String.IsNullOrWhiteSpace(stderr) then stdout
                            else stderr

                        return Error (msg.Trim())
            with ex ->
                logger.error $"Process execution failed: {ex.Message}"
                return Error ex.Message
        }

    let private windowsInstall (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            let exePath = getExePath ()

            let args =
                $"""/create /tn "{windowsTaskName}" /tr "\"{exePath}\" service run" /sc onlogon /rl highest /f"""

            logger.info $"Registering Windows task: {windowsTaskName}"
            let! result = runProcess logger "schtasks" args

            match result with
            | Ok _ ->
                logger.info "Windows task registered."
                return Installed
            | Error msg ->
                logger.error $"Failed to register task: {msg}"
                return Failed msg
        }

    let private windowsUninstall (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            let args = $"""/delete /tn "{windowsTaskName}" /f"""
            logger.info $"Removing Windows task: {windowsTaskName}"
            let! result = runProcess logger "schtasks" args

            match result with
            | Ok _ ->
                logger.info "Windows task removed."
                return Uninstalled
            | Error msg ->
                if msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase) then
                    return NotInstalled
                else
                    return Failed msg
        }

    let private windowsStatus (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            let args = $"""/query /tn "{windowsTaskName}" /fo csv /nh"""
            let! result = runProcess logger "schtasks" args

            match result with
            | Ok output ->
                if String.IsNullOrWhiteSpace(output) then
                    return NotInstalled
                else
                    return StatusInfo $"Task '{windowsTaskName}': {output.Trim()}"
            | Error msg ->
                if msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase) then
                    return NotInstalled
                else
                    return Failed msg
        }

    // ─── macOS: launchd ─────────────────────────────────────────────

    let private launchdLabel = "com.hermes.service"

    let private plistPath () : string =
        let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        Path.Combine(home, "Library", "LaunchAgents", $"{launchdLabel}.plist")

    let generatePlist (exePath: string) : string =
        $"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>{launchdLabel}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{exePath}</string>
        <string>service</string>
        <string>run</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/tmp/hermes-stdout.log</string>
    <key>StandardErrorPath</key>
    <string>/tmp/hermes-stderr.log</string>
</dict>
</plist>
"""

    let private macInstall (fs: Algebra.FileSystem) (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            let path = plistPath ()
            let dir = Path.GetDirectoryName(path)

            match dir with
            | null -> return Failed "Could not determine LaunchAgents directory"
            | d ->
                if not (fs.directoryExists d) then
                    fs.createDirectory d

                let exePath = getExePath ()
                let plist = generatePlist exePath
                do! fs.writeAllText path plist
                logger.info $"launchd plist written to: {path}"

                let! result = runProcess logger "launchctl" $"load {path}"

                match result with
                | Ok _ ->
                    logger.info "launchd agent loaded."
                    return Installed
                | Error msg ->
                    logger.warn $"Plist written but load failed: {msg}"
                    return Installed
        }

    let private macUninstall (fs: Algebra.FileSystem) (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            let path = plistPath ()

            if not (fs.fileExists path) then
                return NotInstalled
            else
                let! _ = runProcess logger "launchctl" $"unload {path}"
                fs.deleteFile path
                logger.info "launchd agent unloaded and plist removed."
                return Uninstalled
        }

    let private macStart (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            let! result = runProcess logger "launchctl" $"start {launchdLabel}"

            match result with
            | Ok _ -> return Started
            | Error msg -> return Failed msg
        }

    let private macStop (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            let! result = runProcess logger "launchctl" $"stop {launchdLabel}"

            match result with
            | Ok _ -> return Stopped
            | Error msg -> return Failed msg
        }

    let private macStatus (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            let! result = runProcess logger "launchctl" $"list {launchdLabel}"

            match result with
            | Ok output ->
                if String.IsNullOrWhiteSpace(output) then
                    return NotInstalled
                else
                    return StatusInfo $"launchd agent '{launchdLabel}': {output.Trim()}"
            | Error msg ->
                if msg.Contains("Could not find service", StringComparison.OrdinalIgnoreCase) then
                    return NotInstalled
                else
                    return Failed msg
        }

    // ─── Public API ─────────────────────────────────────────────────

    /// Install the service for auto-start on the current platform.
    let install (fs: Algebra.FileSystem) (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            match detectPlatform () with
            | Windows -> return! windowsInstall logger
            | MacOS -> return! macInstall fs logger
            | Unsupported -> return Failed "Unsupported platform"
        }

    /// Uninstall the service registration.
    let uninstall (fs: Algebra.FileSystem) (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            match detectPlatform () with
            | Windows -> return! windowsUninstall logger
            | MacOS -> return! macUninstall fs logger
            | Unsupported -> return Failed "Unsupported platform"
        }

    /// Start the service via the platform service manager.
    let start (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            match detectPlatform () with
            | Windows ->
                let args = $"""/run /tn "{windowsTaskName}" """
                let! result = runProcess logger "schtasks" args

                match result with
                | Ok _ -> return Started
                | Error msg -> return Failed msg
            | MacOS -> return! macStart logger
            | Unsupported -> return Failed "Unsupported platform"
        }

    /// Stop the service via the platform service manager.
    let stop (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            match detectPlatform () with
            | Windows ->
                let args = $"""/end /tn "{windowsTaskName}" """
                let! result = runProcess logger "schtasks" args

                match result with
                | Ok _ -> return Stopped
                | Error msg -> return Failed msg
            | MacOS -> return! macStop logger
            | Unsupported -> return Failed "Unsupported platform"
        }

    /// Query service status from the platform service manager.
    let status (logger: Algebra.Logger) : Task<ServiceResult> =
        task {
            match detectPlatform () with
            | Windows -> return! windowsStatus logger
            | MacOS -> return! macStatus logger
            | Unsupported -> return Failed "Unsupported platform"
        }

    /// Format a ServiceResult for display.
    let formatResult (result: ServiceResult) : string =
        match result with
        | Installed -> "Service installed successfully."
        | Uninstalled -> "Service uninstalled successfully."
        | Started -> "Service started."
        | Stopped -> "Service stopped."
        | AlreadyInstalled -> "Service is already installed."
        | AlreadyRunning -> "Service is already running."
        | NotInstalled -> "Service is not installed."
        | NotRunning -> "Service is not running."
        | StatusInfo info -> info
        | Failed msg -> $"Failed: {msg}"
