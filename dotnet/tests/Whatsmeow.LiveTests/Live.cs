namespace Whatsmeow.LiveTests;

/// <summary>
/// Every live test: one after the other, never in parallel, on one <see cref="LiveSidecar"/> (the live database has a
/// single owner, ADR 0020) and one <see cref="LiveSender"/>.
/// </summary>
[CollectionDefinition(nameof(Live), DisableParallelization = true)]
public sealed class Live : ICollectionFixture<LiveSidecar>, ICollectionFixture<LiveSender>;
