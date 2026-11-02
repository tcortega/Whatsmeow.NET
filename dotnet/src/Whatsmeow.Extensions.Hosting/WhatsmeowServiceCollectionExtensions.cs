using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whatsmeow.Sidecar;

namespace Whatsmeow.Extensions.Hosting;

/// <summary>Adds Whatsmeow.NET to a host: one sidecar for the host's lifetime, its sessions and their handlers.</summary>
public static class WhatsmeowServiceCollectionExtensions
{
	/// <summary>The configuration section <see cref="WhatsmeowSidecarOptions"/> is bound from.</summary>
	private const string SectionName = "Whatsmeow";

	/// <summary>
	/// <para>
	/// Adds the sidecar, started when the host starts and stopped when it stops, as a <see cref="WhatsmeowSidecar"/>
	/// singleton for sessions created at runtime. It starts in <c>IHostedLifecycleService.StartingAsync</c>: an
	/// <c>IHostedLifecycleService</c> registered before this call sees it not started yet in its own
	/// <c>StartingAsync</c>; every hosted service's <c>StartAsync</c> sees it running.
	/// </para>
	/// <para>
	/// <see cref="WhatsmeowSidecarOptions"/> is bound from the <c>Whatsmeow</c> configuration section and validated when
	/// the host starts, with the same rules as <see cref="WhatsmeowSidecar.StartAsync(WhatsmeowSidecarOptions, CancellationToken)"/>,
	/// which throw <see cref="OptionsValidationException"/>. A value the binder cannot convert, such as a
	/// <see cref="WhatsmeowSidecarOptions.Transport"/> that is no name or number, fails first, with the binder's
	/// <see cref="InvalidOperationException"/>. One difference from the no-DI path: a relative
	/// <see cref="WhatsmeowSidecarOptions.DatabasePath"/> or <see cref="WhatsmeowSidecarOptions.MediaRoot"/>, and the
	/// default database file, are in the host's content root, not the current directory, which for a Windows service
	/// is <c>System32</c>.
	/// </para>
	/// <para>
	/// The sidecar uses the <see cref="ILoggerFactory"/> and <see cref="TimeProvider"/> from the container when there
	/// are ones. Calling it again adds nothing: there is one sidecar.
	/// </para>
	/// </summary>
	/// <returns>A builder to declare sessions and register handlers.</returns>
	public static WhatsmeowBuilder AddWhatsmeow(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);
		if (services.Any(static descriptor => descriptor.ServiceType == typeof(WhatsmeowSidecar)))
			return new WhatsmeowBuilder(services);

		_ = services
			.AddOptions<WhatsmeowSidecarOptions>()
			.BindConfiguration(SectionName)
			.PostConfigure<IHostEnvironment>(
				static (options, environment) =>
				{
					InContentRoot(options, environment.ContentRootPath);
					WhatsmeowSidecarOptionsSetup.Apply(options);
				}
			)
			.ValidateOnStart();
		services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WhatsmeowSidecarOptions>, WhatsmeowSidecarOptionsValidator>());
		_ = services.AddSingleton(CreateSidecar);
		_ = services.AddHostedService<WhatsmeowHostedService>();
		return new WhatsmeowBuilder(services);
	}

	/// <summary>The sidecar, not started yet: the clients of its sessions exist before the host starts it (ADR 0034).</summary>
	private static WhatsmeowSidecar CreateSidecar(IServiceProvider services)
	{
		var handlers = services.GetServices<HandlerRegistration>().ToArray();
		var declared = services.GetServices<DeclaredSession>().Select(static session => session.Id).ToHashSet();
		return WhatsmeowSidecar.Create(
			services.GetRequiredService<IOptions<WhatsmeowSidecarOptions>>().Value,
			services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance,
			services.GetService<TimeProvider>(),
			services.GetService<FakeWhatsAppMode>() is not null,
			client =>
			{
				foreach (var handler in handlers)
					handler.AttachTo(client, services);
			},
			declared
		);
	}

	/// <summary>
	/// The one difference from the no-DI path (ADR 0034): relative paths, and the default database file, are the
	/// content root's. A blank path stays blank, for the rules to report.
	/// </summary>
	private static void InContentRoot(WhatsmeowSidecarOptions options, string contentRoot)
	{
		options.DatabasePath = InContentRoot(options.DatabasePath ?? WhatsmeowSidecarOptionsSetup.DefaultDatabaseFile, contentRoot);
		if (options.MediaRoot is { } mediaRoot)
			options.MediaRoot = InContentRoot(mediaRoot, contentRoot);
	}

	private static string InContentRoot(string path, string contentRoot) =>
		string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path) ? path : Path.Combine(contentRoot, path);
}
