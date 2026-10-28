using Xunit;

namespace Hermes.Servicing.WindowsTests;

// These tests observe or temporarily affect the shared TestHost primary token.
// DisableParallelization also excludes concurrently running other collections.
[CollectionDefinition(nameof(RestrictedOwnerNativeCollection), DisableParallelization = true)]
public sealed class RestrictedOwnerNativeCollection
{
}
