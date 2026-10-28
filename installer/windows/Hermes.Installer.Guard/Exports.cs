using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hermes.Installer.Control;
using Hermes.Servicing;

namespace Hermes.Installer.Guard;

[SupportedOSPlatform("windows")]
public static class Exports
{
    [UnmanagedCallersOnly(
        EntryPoint = "HermesPreflight",
        CallConvs = new[] { typeof(CallConvStdcall) })]
    public static uint Preflight(uint session) =>
        WindowsMsiGuard.Invoke(session, GuardPhase.Preflight);

    [UnmanagedCallersOnly(
        EntryPoint = "HermesTransactionFence",
        CallConvs = new[] { typeof(CallConvStdcall) })]
    public static uint TransactionFence(uint session) =>
        WindowsMsiGuard.Invoke(session, GuardPhase.Transaction);
}
