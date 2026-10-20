using System.ComponentModel;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class ComponentClientInventoryTests
{
    [Theory]
    [InlineData(259)]
    [InlineData(1607)]
    public void KnownInitialNoClientResultsProduceAnEmptyInventory(int code)
    {
        var component = Guid.NewGuid();
        var native = new ScriptedClients(new MsiClientResult((uint)code, ""));
        Assert.Empty(ComponentClientInventory.Read(component, native));
        Assert.Equal(new[] { (component, 0u) }, native.Calls);
    }

    [Fact]
    public void RegisteredClientsAreEnumeratedInOrderUntilNoMoreItems()
    {
        var component = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var native = new ScriptedClients(
            new(0, first.ToString("B")), new(0, second.ToString("B")), new(259, ""));
        Assert.Equal(new[] { first, second }, ComponentClientInventory.Read(component, native));
        Assert.Equal(new[] { (component, 0u), (component, 1u), (component, 2u) }, native.Calls);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(87, false)]
    [InlineData(234, false)]
    [InlineData(234, true)]
    [InlineData(1605, false)]
    [InlineData(1605, true)]
    [InlineData(1606, false)]
    [InlineData(1607, true)]
    [InlineData(1610, false)]
    [InlineData(1610, true)]
    public void NativeFailuresNeverReturnAnEmptyOrPartialInventory(int code, bool partial)
    {
        MsiClientResult failure = new((uint)code, "");
        var native = partial
            ? new ScriptedClients(new(0, Guid.NewGuid().ToString("B")), failure)
            : new ScriptedClients(failure);
        var error = Assert.Throws<Win32Exception>(() =>
            ComponentClientInventory.Read(Guid.NewGuid(), native));
        Assert.Equal(code, error.NativeErrorCode);
        Assert.Equal(partial ? 2 : 1, native.Calls.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-product-code")]
    [InlineData("{00000000-0000-0000-0000-000000000000}")]
    public void SuccessfulNativeResultRequiresARealProductIdentity(string product)
    {
        var native = new ScriptedClients(new(0, product), new(259, ""));
        var error = Assert.Throws<SetupIoException>(() =>
            ComponentClientInventory.Read(Guid.NewGuid(), native));
        Assert.Equal(SetupCode.OwnershipRefused, error.Code);
        Assert.Single(native.Calls);
    }

    [Fact]
    public void DuplicateNativeClientsAreAmbiguousRatherThanOwnershipEvidence()
    {
        var product = Guid.NewGuid().ToString("B");
        var native = new ScriptedClients(new(0, product), new(0, product), new(259, ""));
        var error = Assert.Throws<SetupIoException>(() =>
            ComponentClientInventory.Read(Guid.NewGuid(), native));
        Assert.Equal(SetupCode.OwnershipRefused, error.Code);
        Assert.Equal(2, native.Calls.Count);
    }

    [Fact]
    public void EnumerationRetainsItsExistingFiniteBound()
    {
        var results = Enumerable.Range(1, 10_001).Select(index =>
            new MsiClientResult(0, new Guid(index, 0, 0, new byte[8]).ToString("B"))).ToArray();
        var native = new ScriptedClients(results);
        var error = Assert.Throws<SetupIoException>(() =>
            ComponentClientInventory.Read(Guid.NewGuid(), native));
        Assert.Equal(SetupCode.OwnershipRefused, error.Code);
        Assert.Equal(10_001, native.Calls.Count);
        Assert.Equal(10_000u, native.Calls[^1].Index);
    }

    private sealed class ScriptedClients(params MsiClientResult[] results) : IMsiComponentClients
    {
        internal List<(Guid Component, uint Index)> Calls { get; } = [];

        public MsiClientResult Enumerate(Guid component, uint index)
        {
            Calls.Add((component, index));
            return results[checked((int)index)];
        }
    }
}
