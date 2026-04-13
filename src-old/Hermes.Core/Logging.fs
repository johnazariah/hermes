namespace Hermes.Core

open System.IO
open Serilog
open Serilog.Events

/// Structured logging setup using Serilog.
/// Returns an Algebra.Logger record so callers never depend on Serilog directly.
[<RequireQualifiedAccess>]
module Logging =

    /// Build a Serilog ILogger, then wrap it in the Logger algebra.
    let configure (configDir: string) (minLevel: LogEventLevel) : Algebra.Logger =
        let logDir = Path.Combine(configDir, "logs")
        Directory.CreateDirectory(logDir) |> ignore
        let logPath = Path.Combine(logDir, "hermes-.log")

        let serilog =
            LoggerConfiguration()
                .MinimumLevel.Is(minLevel)
                .WriteTo.Console(
                    outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .WriteTo.File(
                    logPath,
                    rollingInterval = RollingInterval.Day,
                    retainedFileCountLimit = 14,
                    outputTemplate =
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger()

        { Algebra.Logger.info = fun msg -> serilog.Information(msg)
          warn = fun msg -> serilog.Warning(msg)
          error = fun msg -> serilog.Error(msg)
          debug = fun msg -> serilog.Debug(msg) }

    /// Configure with defaults for the platform config directory.
    let configureDefault () =
        configure (Config.configDir Interpreters.systemEnvironment) LogEventLevel.Information

    /// A silent logger that discards all messages (useful for tests).
    let silent : Algebra.Logger =
        { info = ignore
          warn = ignore
          error = ignore
          debug = ignore }
