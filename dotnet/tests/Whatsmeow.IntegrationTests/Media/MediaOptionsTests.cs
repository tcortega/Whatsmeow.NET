using Microsoft.Extensions.Options;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Media;

/// <summary>A media root that is not an existing directory fails the start before anything is spawned (docs/guidelines/options.md).</summary>
public sealed class MediaOptionsTests(SidecarBinaries binaries)
{
	[Theory(Timeout = 60_000)]
	[InlineData("missing")]
	[InlineData("a file")]
	[InlineData("white space")]
	public async Task Start_AMediaRootThatIsNotADirectory_FailsBeforeSpawningAnything(string root)
	{
		var stub = binaries.Stage(binaries.HangingStub);
		var directory = binaries.NewDirectory();
		var file = Path.Combine(directory, "file");
		await File.WriteAllTextAsync(file, "", TestContext.Current.CancellationToken);
		var options = new WhatsmeowSidecarOptions
		{
			SidecarPath = stub,
			MediaRoot = root switch
			{
				"missing" => Path.Combine(directory, "missing"),
				"a file" => file,
				_ => "  ",
			},
		};

		_ = await Assert.ThrowsAsync<OptionsValidationException>(() => WhatsmeowSidecar.StartAsync(options, TestContext.Current.CancellationToken));

		Assert.False(Processes.StubStarted(stub));
	}
}
