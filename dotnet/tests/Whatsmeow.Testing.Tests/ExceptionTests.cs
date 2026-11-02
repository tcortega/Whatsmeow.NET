namespace Whatsmeow.Testing.Tests;

/// <summary>A library exception a test builds, for <c>FailNext</c>, carries what the library's own carries (ADR 0036).</summary>
public sealed class ExceptionTests
{
	[Fact]
	public void ExceptionOfOneReason_CarriesItsCode_FromEveryConstructor()
	{
		var cause = new InvalidOperationException("cause");
		(WhatsmeowException[] Built, WhatsmeowErrorCode Code)[] cases =
		[
			([new RecipientNotFoundException(), new RecipientNotFoundException("x"), new RecipientNotFoundException("x", cause)], WhatsmeowErrorCode.RecipientNotOnWhatsApp),
			([new RateLimitedException(), new RateLimitedException("x"), new RateLimitedException("x", cause)], WhatsmeowErrorCode.RateLimited),
			([new MediaExpiredException(), new MediaExpiredException("x"), new MediaExpiredException("x", cause)], WhatsmeowErrorCode.MediaExpired),
			([new NotLoggedInException(), new NotLoggedInException("x"), new NotLoggedInException("x", cause)], WhatsmeowErrorCode.NotLoggedIn),
			([new SessionLoggedOutException(), new SessionLoggedOutException("x"), new SessionLoggedOutException("x", cause)], WhatsmeowErrorCode.LoggedOut),
			([new SessionNotFoundException(), new SessionNotFoundException("x"), new SessionNotFoundException("x", cause)], WhatsmeowErrorCode.SessionNotFound),
			([new OutcomeUnknownException(), new OutcomeUnknownException("x"), new OutcomeUnknownException("x", cause)], WhatsmeowErrorCode.OutcomeUnknown),
			([new WhatsmeowValidationException(), new WhatsmeowValidationException("x"), new WhatsmeowValidationException("x", cause)], WhatsmeowErrorCode.InvalidArgument),
			([new WhatsAppErrorException(), new WhatsAppErrorException("x"), new WhatsAppErrorException("x", cause)], WhatsmeowErrorCode.WhatsAppError),
			([new TemporarilyBannedException(), new TemporarilyBannedException("x"), new TemporarilyBannedException("x", cause)], WhatsmeowErrorCode.TemporarilyBanned),
		];

		Assert.All(cases, c => Assert.All(c.Built, built => Assert.Equal(c.Code, built.ErrorCode)));
	}

	[Fact]
	public void TheCodeAnExceptionIsBuiltWith_OverridesItsOwn() =>
		Assert.Equal(WhatsmeowErrorCode.Internal, new RateLimitedException("x") { ErrorCode = WhatsmeowErrorCode.Internal }.ErrorCode);
}
