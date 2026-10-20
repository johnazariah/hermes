using System.Buffers.Binary;
using System.Runtime.Versioning;
using Hermes.Servicing;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Hermes.Servicing.Tests;

[SupportedOSPlatform("windows")]
public sealed class PhysicalRefusalEvidenceTests
{
    private const uint Reparse = 0x00200000;
    private static readonly string Root = Path.GetFullPath("synthetic-physical-root");
    private static readonly string Image = Path.GetFullPath("synthetic-physical-source/Hermes.Service.dll");

    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    public void Image_open_failure_records_captured_error_without_further_calls(int error)
    {
        var api = Api(open: (_, _) => Missing(error),
            information: _ => throw new InvalidOperationException("Unexpected information query."));
        Assert.Equal(Expected(StartupEvidenceStage.LoadedImage,
            StartupEvidenceOperation.OpenFollowing, StartupEvidenceFailure.NativeCallFailed, error),
            Failure(() => new WindowsPhysicalPaths(api).Image(Image)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Standard_information_failure_identifies_root_or_image(bool directory)
    {
        var api = Api(information: _ => new(false, Bytes(), 5));
        var paths = new WindowsPhysicalPaths(api);
        var evidence = Failure(() => directory ? paths.Directory(Root) : paths.Image(Image));
        Assert.Equal(Expected(
            directory ? StartupEvidenceStage.ReservedRoot : StartupEvidenceStage.LoadedImage,
            StartupEvidenceOperation.StandardInformation,
            StartupEvidenceFailure.NativeCallFailed, 5), evidence);
    }

    [Fact]
    public void Delete_pending_is_not_reported_as_a_native_call_error()
    {
        var api = Api(information: _ => new(true, Bytes(deletePending: true), 1234));
        Assert.Equal(Expected(StartupEvidenceStage.LoadedImage,
            StartupEvidenceOperation.StandardInformation, StartupEvidenceFailure.DeletePending),
            Failure(() => new WindowsPhysicalPaths(api).Image(Image)));
    }

    [Theory]
    [InlineData(0u, StartupEvidenceFailure.NativeCallFailed, 1234)]
    [InlineData(32768u, StartupEvidenceFailure.InvalidReturnedLength, null)]
    [InlineData(40000u, StartupEvidenceFailure.InvalidReturnedLength, null)]
    public void Final_path_failure_distinguishes_failed_call_from_length(
        uint length, StartupEvidenceFailure failure, int? error)
    {
        var api = Api(finalPath: _ => new(length, "", 1234));
        Assert.Equal(Expected(StartupEvidenceStage.LoadedImage,
            StartupEvidenceOperation.FinalPath, failure, error),
            Failure(() => new WindowsPhysicalPaths(api).Image(Image)));
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(false, 2u)]
    [InlineData(true, 1u)]
    public void Directory_or_ambiguous_link_count_is_still_invalid_state(bool directory, uint links)
    {
        var api = Api(information: _ => new(true, Bytes(directory, links: links), 0));
        AssertInvalid(() => new WindowsPhysicalPaths(api).Image(Image));
    }

    [Fact]
    public void Unexpected_final_path_prefix_is_still_invalid_state()
    {
        var api = Api(finalPath: _ => new(8, "unexpected", 0));
        AssertInvalid(() => new WindowsPhysicalPaths(api).Image(Image));
    }

    [Fact]
    public void Successful_image_ignores_nonfailure_error_values()
    {
        var api = Api(
            open: (_, _) => new(Handle(), 1234),
            information: _ => new(true, Bytes(), 1234),
            finalPath: _ => new((uint)Image.Length + 4, @"\\?\" + Image, 1234));
        Assert.Equal(Image, new WindowsPhysicalPaths(api).Image(Image));
    }

    [Fact]
    public void Successful_directory_keeps_original_probe_sequence()
    {
        var calls = new List<string>();
        var api = Api(
            open: (_, _) => { calls.Add("open"); return new(Handle(), 0); },
            information: _ => { calls.Add("information"); return new(true, Bytes(true), 0); },
            finalPath: _ => { calls.Add("final"); return new(12, @"\\?\" + Root, 0); });
        Assert.Equal(Root, new WindowsPhysicalPaths(api).Directory(Root));
        Assert.Equal(new[] { "open", "information", "final" }, calls);
    }

    [Fact]
    public void Nondirectory_reserved_root_is_still_invalid_state()
    {
        AssertInvalid(() => new WindowsPhysicalPaths(Api()).Directory(Root));
    }

    [Fact]
    public void Directory_open_failure_retains_native_error()
    {
        var api = Api(open: (_, _) => Missing(5));
        Assert.Equal(Expected(StartupEvidenceStage.ReservedRoot,
            StartupEvidenceOperation.OpenFollowing, StartupEvidenceFailure.NativeCallFailed, 5),
            Failure(() => new WindowsPhysicalPaths(api).Directory(Root)));
    }

    [Fact]
    public void Missing_filesystem_root_is_not_positive_absence()
    {
        var root = Path.GetPathRoot(Root)
            ?? throw new InvalidOperationException("Synthetic absolute path has no root.");
        var api = Api(open: (_, _) => Missing(2));
        Assert.Equal(Expected(StartupEvidenceStage.ReservedRoot,
            StartupEvidenceOperation.OpenFollowing, StartupEvidenceFailure.NoParent, 2),
            Failure(() => new WindowsPhysicalPaths(api).Directory(root)));
    }

    [Fact]
    public void Dangling_entry_is_distinguished_from_native_failure()
    {
        var api = Api(open: (_, flags) => flags == Reparse
            ? new(Handle(), 1234) : Missing(3));
        Assert.Equal(Expected(StartupEvidenceStage.ReservedRoot,
            StartupEvidenceOperation.OpenEntry, StartupEvidenceFailure.ExistingEntry),
            Failure(() => new WindowsPhysicalPaths(api).Directory(Root)));
    }

    [Fact]
    public void Nonfollowing_open_failure_retains_its_own_error()
    {
        var api = Api(open: (_, flags) => Missing(flags == Reparse ? 5 : 3));
        Assert.Equal(Expected(StartupEvidenceStage.ReservedRoot,
            StartupEvidenceOperation.OpenEntry, StartupEvidenceFailure.NativeCallFailed, 5),
            Failure(() => new WindowsPhysicalPaths(api).Directory(Root)));
    }

    [Fact]
    public void Missing_directory_uses_only_existing_following_nonfollowing_and_parent_probes()
    {
        var parent = Path.GetDirectoryName(Root)
            ?? throw new InvalidOperationException("Synthetic directory has no parent.");
        var calls = new List<(string Path, uint Flags)>();
        var api = Api(
            open: (path, flags) =>
            {
                calls.Add((path, flags));
                return path == Root ? Missing(3) : new(Handle(), 0);
            },
            information: _ => new(true, Bytes(true), 0),
            finalPath: _ => new(12, @"\\?\" + parent, 0));
        Assert.Equal(Root, new WindowsPhysicalPaths(api).Directory(Root));
        Assert.Equal(new[] { (Root, 0u), (Root, Reparse), (parent, 0u) }, calls);
    }

    [Fact]
    public void Ancestor_failure_keeps_depth()
    {
        var api = Api(open: (path, _) => Missing(path == Root ? 3 : 5));
        Assert.Equal(Expected(StartupEvidenceStage.ReservedRoot,
            StartupEvidenceOperation.OpenFollowing,
            StartupEvidenceFailure.NativeCallFailed, 5, 1),
            Failure(() => new WindowsPhysicalPaths(api).Directory(Root)));
    }

    [Fact]
    public void Ancestor_bound_remains_invalid_state_without_invented_evidence()
    {
        var deep = Path.Combine(
            new[] { Root }.Concat(Enumerable.Repeat("segment", 66)).ToArray());
        AssertInvalid(() => new WindowsPhysicalPaths(
            Api(open: (_, _) => Missing(3))).Directory(deep));
    }

    private static StartupRefusalEvidence Expected(
        StartupEvidenceStage stage, StartupEvidenceOperation operation,
        StartupEvidenceFailure failure, int? error = null, int position = 0) =>
        new(stage, operation, failure,
            error is null ? StartupEvidenceErrorKind.None : StartupEvidenceErrorKind.Win32,
            error ?? 0, position);

    private static StartupRefusalEvidence Failure(Func<string> action)
    {
        var error = Assert.Throws<AdmissionException>(() => { action(); });
        Assert.Equal(RefusalReason.Unavailable, error.Reason);
        return Assert.IsType<StartupRefusalEvidence>(error.Evidence);
    }

    private static void AssertInvalid(Func<string> action)
    {
        var error = Assert.Throws<AdmissionException>(() => { action(); });
        Assert.Equal(RefusalReason.InvalidState, error.Reason);
        Assert.Null(error.Evidence);
    }

    private static SafeFileHandle Handle() => new((nint)123, ownsHandle: false);

    private static NativePathOpen Missing(int error) =>
        new(new SafeFileHandle((nint)(-1), ownsHandle: false), error);

    private static byte[] Bytes(bool directory = false, bool deletePending = false, uint links = 1)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), links);
        bytes[20] = deletePending ? (byte)1 : (byte)0;
        bytes[21] = directory ? (byte)1 : (byte)0;
        return bytes;
    }

    private static IWindowsPathApi Api(
        Func<string, uint, NativePathOpen>? open = null,
        Func<SafeFileHandle, NativePathInformation>? information = null,
        Func<SafeFileHandle, NativePathFinal>? finalPath = null) =>
        new DelegateApi(
            open ?? ((_, _) => new(Handle(), 0)),
            information ?? (_ => new(true, Bytes(), 0)),
            finalPath ?? (_ => new((uint)Image.Length + 4, @"\\?\" + Image, 0)));

    private sealed class DelegateApi(
        Func<string, uint, NativePathOpen> open,
        Func<SafeFileHandle, NativePathInformation> information,
        Func<SafeFileHandle, NativePathFinal> finalPath) : IWindowsPathApi
    {
        public NativePathOpen Open(string path, uint flags) => open(path, flags);
        public NativePathInformation Information(SafeFileHandle handle) => information(handle);
        public NativePathFinal FinalPath(SafeFileHandle handle) => finalPath(handle);
    }
}
