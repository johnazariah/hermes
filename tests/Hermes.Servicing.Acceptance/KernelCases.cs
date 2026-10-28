namespace Hermes.Servicing.Acceptance;

internal sealed class KernelFixture
{
    internal string Root { get; }
    internal string App => Path.Combine(Root, "App");
    internal string Service => Path.Combine(App, "service");
    internal string Gate => Path.Combine(Root, "admission.bin");
    internal string Probe => Path.Combine(Service, "Hermes.Service.exe");

    internal KernelFixture(AcceptanceContext context)
    {
        Root = Path.Combine(context.Options.Work, "kernel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Service);
        Clone(context.Options.ProbeOld, Service);
        Publish();
    }

    private static void Clone(string source, string destination)
    {
        TemporaryPaths.RequirePlainDirectory(source);
        foreach (var directory in Directory.EnumerateDirectories(source, "*",
            SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            OwnerFixtureStore.RequireRegular(File.GetAttributes(file));
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    internal void Publish()
    {
        var images = Directory.EnumerateFiles(Service, "Hermes.*.dll")
            .Order(StringComparer.Ordinal)
            .Select(path => new ManagedImage(
                "service/" + Path.GetFileName(path), Files.Mvid(path)))
            .ToImmutableArray();
        using var gate = ExclusiveGate.Open(Gate, create: true);
        gate.PublishReady(new(new(
            new(HermesServicingProtocol.UpgradeCode, App, HermesServicingProtocol.AdmissionAbi),
            new Guid("510ca935-a545-4b31-b27b-f26a0bec2926"), images)));
    }

    internal OwnedChild Start(string name, string mixed = "-") =>
        OwnedChild.Start(Probe,
            [App, Gate, Path.Combine(Root, name + ".effect"), mixed], Root);

    internal void ReplaceMappedEntry(string newProbe)
    {
        var target = Path.Combine(Service, "Hermes.Service.dll");
        File.Move(target, target + ".mapped-old");
        File.Copy(Path.Combine(newProbe, "Hermes.Service.dll"), target);
        Publish();
    }

    internal void Remove() => Directory.Delete(Root, recursive: true);
}

internal static class KernelCases
{
    internal static async Task<CaseReport> RunAsync(
        AcceptanceContext context, CancellationToken cancellationToken)
    {
        var evidence = ImmutableArray.CreateBuilder<object>();
        await LifetimeAsync(context, evidence, cancellationToken);
        await ExclusiveWinsAsync(context, evidence, cancellationToken);
        await InvalidInsideAsync(context, evidence, cancellationToken);
        await MixedAsync(context, evidence, cancellationToken);
        await MappedGenerationAsync(context, evidence, cancellationToken);
        return new("windows-kernel-admission", "production-component",
            CaseState.Passed, evidence.ToImmutable());
    }

    private static async Task<ProbeMessage> MappedAsync(
        OwnedChild child, CancellationToken cancellationToken)
    {
        var message = EvidenceJson.Decode<ProbeMessage>(
            await child.ReadAsync(cancellationToken));
        Require.That(message.Kind == "mapped" && message.Process == child.Identity);
        Require.That(!message.EffectExists);
        return message;
    }

    private static async Task<ProbeMessage> EnterAsync(
        OwnedChild child, string outcome, string? reason,
        CancellationToken cancellationToken)
    {
        await child.SendAsync("enter", cancellationToken);
        var message = EvidenceJson.Decode<ProbeMessage>(
            await child.ReadAsync(cancellationToken));
        Require.That(message.Kind == "entered" && message.Process == child.Identity);
        Require.That(message.Admission is { AcquiringThreadExited: true } admission &&
            admission.Outcome == outcome && admission.Reason == reason &&
            !admission.LeaseDisposable);
        Require.That(message.EffectExists == (outcome is "Admitted" or "Outside"));
        return message;
    }

    private static async Task LifetimeAsync(AcceptanceContext context,
        ImmutableArray<object>.Builder evidence, CancellationToken cancellationToken)
    {
        var fixture = new KernelFixture(context);
        var before = GateReader.Read(fixture.Gate);
        using var first = fixture.Start("first");
        using var survivor = fixture.Start("independent-service-component");
        evidence.Add(await MappedAsync(first, cancellationToken));
        evidence.Add(await EnterAsync(first, "Admitted", null, cancellationToken));
        evidence.Add(await MappedAsync(survivor, cancellationToken));
        evidence.Add(await EnterAsync(survivor, "Admitted", null, cancellationToken));
        Denied(fixture.Gate);
        RequireSharingDenial(() => File.Delete(fixture.Gate));
        RequireSharingDenial(() => File.Move(fixture.Gate, fixture.Gate + ".replacement"));
        await first.StopProbeAsync(cancellationToken);
        Denied(fixture.Gate);
        await survivor.StopProbeAsync(cancellationToken);
        using (ExclusiveGate.Open(fixture.Gate)) { }
        Require.That(GateReader.Read(fixture.Gate) == before);
        evidence.Add(new { boundary = "thread-death-gc-independent-process-exit", before });
        fixture.Remove();
    }

    private static void Denied(string path) =>
        RequireSharingDenial(() =>
        {
            using var unexpected = ExclusiveGate.Open(path);
            throw new InvalidDataException("Exclusive admission unexpectedly succeeded.");
        });

    private static void RequireSharingDenial(Action action)
    {
        try { action(); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return; }
        throw new InvalidDataException("Expected a Windows kernel sharing denial.");
    }

    private static async Task ExclusiveWinsAsync(AcceptanceContext context,
        ImmutableArray<object>.Builder evidence, CancellationToken cancellationToken)
    {
        var fixture = new KernelFixture(context);
        var before = GateReader.Read(fixture.Gate);
        using (var exclusive = ExclusiveGate.Open(fixture.Gate))
        {
            exclusive.CloseAdmission();
            using var busy = fixture.Start("busy");
            evidence.Add(await MappedAsync(busy, cancellationToken));
            evidence.Add(await EnterAsync(busy, "Refused", "Busy", cancellationToken));
            await busy.StopProbeAsync(cancellationToken);
        }
        using var closed = fixture.Start("closed");
        evidence.Add(await MappedAsync(closed, cancellationToken));
        evidence.Add(await EnterAsync(closed, "Refused", "Closed", cancellationToken));
        await closed.StopProbeAsync(cancellationToken);
        var after = GateReader.Read(fixture.Gate);
        Require.That(before.FileIdentity == after.FileIdentity && after.HeaderState == "Closed");
        evidence.Add(new { boundary = "exclusive-wins-durable-closed", before, after });
        fixture.Remove();
    }

    private static async Task InvalidInsideAsync(AcceptanceContext context,
        ImmutableArray<object>.Builder evidence, CancellationToken cancellationToken)
    {
        var fixture = new KernelFixture(context);
        File.WriteAllBytes(fixture.Gate, [0x48, 0x52, 0x4d]);
        using var child = fixture.Start("invalid-inside");
        evidence.Add(await MappedAsync(child, cancellationToken));
        evidence.Add(await EnterAsync(child, "Refused", "InvalidState", cancellationToken));
        await child.StopProbeAsync(cancellationToken);
        evidence.Add(GateReader.Read(fixture.Gate));
        fixture.Remove();
    }

    private static async Task MixedAsync(AcceptanceContext context,
        ImmutableArray<object>.Builder evidence, CancellationToken cancellationToken)
    {
        var fixture = new KernelFixture(context);
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var mixed = Path.Combine(outside, "Hermes.Setup.dll");
        File.Copy(typeof(WindowsSetupFactory).Assembly.Location, mixed);
        using var child = fixture.Start("mixed", mixed);
        var mapped = await MappedAsync(child, cancellationToken);
        Require.That(mapped.Loaded.Images.Any(image => InstalledSource.SamePath(image.AssemblyPath, mixed)));
        evidence.Add(mapped);
        evidence.Add(await EnterAsync(child, "Refused", "WrongGeneration", cancellationToken));
        await child.StopProbeAsync(cancellationToken);
        fixture.Remove();
    }

    private static async Task MappedGenerationAsync(AcceptanceContext context,
        ImmutableArray<object>.Builder evidence, CancellationToken cancellationToken)
    {
        var fixture = new KernelFixture(context);
        using var old = fixture.Start("old-mapped");
        var mapped = await MappedAsync(old, cancellationToken);
        Require.That(mapped.Loaded.EntryModuleVersionId !=
            Files.Mvid(Path.Combine(context.Options.ProbeNew, "Hermes.Service.dll")));
        fixture.ReplaceMappedEntry(context.Options.ProbeNew);
        var refused = await EnterAsync(old, "Refused", "WrongGeneration", cancellationToken);
        Require.That(refused.Loaded.EntryModuleVersionId == mapped.Loaded.EntryModuleVersionId);
        evidence.Add(new { boundary = "old-image-mapped-before-generation-publication", mapped, refused });
        await old.StopProbeAsync(cancellationToken);
        await CurrentGenerationAsync(fixture, evidence, cancellationToken);
        fixture.Remove();
    }

    private static async Task CurrentGenerationAsync(KernelFixture fixture,
        ImmutableArray<object>.Builder evidence, CancellationToken cancellationToken)
    {
        using var current = fixture.Start("current");
        evidence.Add(await MappedAsync(current, cancellationToken));
        var entered = await EnterAsync(current, "Admitted", null, cancellationToken);
        Require.That(entered.Loaded.EntryModuleVersionId ==
            Files.Mvid(Path.Combine(fixture.Service, "Hermes.Service.dll")));
        evidence.Add(entered);
        await current.StopProbeAsync(cancellationToken);
    }
}
