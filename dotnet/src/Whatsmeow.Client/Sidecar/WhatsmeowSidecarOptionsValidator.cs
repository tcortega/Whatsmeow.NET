using Microsoft.Extensions.Options;

namespace Whatsmeow.Sidecar;

/// <summary>
/// The rules of <see cref="WhatsmeowSidecarOptionsSetup.Validate"/> as an <see cref="IValidateOptions{TOptions}"/>, for
/// the hosting package: the DI path and the no-DI path validate with the same rules (docs/guidelines/options.md).
/// Hand-written, because no rule fits the generator (<c>[Range(typeof(TimeSpan), ...)]</c> is trim-unsafe).
/// </summary>
internal sealed class WhatsmeowSidecarOptionsValidator : IValidateOptions<WhatsmeowSidecarOptions>
{
	public ValidateOptionsResult Validate(string? name, WhatsmeowSidecarOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var failures = WhatsmeowSidecarOptionsSetup.Failures(options);
		return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
	}
}
