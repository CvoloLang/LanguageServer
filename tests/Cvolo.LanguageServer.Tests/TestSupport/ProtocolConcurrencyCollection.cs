using Xunit;

namespace Cvolo.LanguageServer.Tests.TestSupport;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProtocolConcurrencyCollection
{
    public const string Name = "Protocol concurrency tests";
}
