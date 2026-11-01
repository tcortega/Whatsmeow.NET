namespace Whatsmeow.IntegrationTests.Support;

/// <summary>The live log's redaction (<see cref="LogRedaction"/>), which no live run can check for itself.</summary>
public sealed class LogRedactionTests
{
	[Fact]
	public void Redact_EveryJidUserPart_AndTheTestNumber_WhateverTheEnvironment()
	{
		const string Line =
			"[16:08:08.582, info] time 2026-11-01T16:08:08-03:00, Successfully paired 15551234567.0:7@s.whatsapp.net, "
			+ "lid 999123456789012:7@lid, user 4915112345678@s.whatsapp.net, typed +1 555 123 4567";

		Assert.Equal(
			"[16:08:08.582, info] time 2026-11-01T16:08:08-03:00, Successfully paired <redacted>.0:7@s.whatsapp.net, "
				+ "lid <redacted>:7@lid, user <redacted>@s.whatsapp.net, typed <redacted>",
			LogRedaction.Redact(Line, "+1 555 123 4567")
		);
		Assert.Equal(
			"paired <redacted>.0:7@s.whatsapp.net at 16:08:08",
			LogRedaction.Redact("paired 15551234567.0:7@s.whatsapp.net at 16:08:08", phoneNumber: "")
		);
	}
}
