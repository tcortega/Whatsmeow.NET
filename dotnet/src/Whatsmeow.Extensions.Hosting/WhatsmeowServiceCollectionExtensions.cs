using Microsoft.Extensions.DependencyInjection.Extensions;
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
	/// Adds the sidecar, started when the host starts and stopped when it stops, as a <see cref="WhatsmeowSidecar"/>
	/// singleton for sessions created at runtime. <see cref="WhatsmeowSidecarOptions"/> is bound from the
	/// <c>Whatsmeow</c> configuration section and validated when the host starts, with the same rules as
	/// <see cref="WhatsmeowSidecar.StartAsync(WhatsmeowSidecarOptions, CancellationToken)"/>. The sidecar uses the
	/// <see cref="ILoggerFactory"/> and <see cref="TimeProvider"/> from the container when there are ones. Calling it
	/// again adds nothing: there is one sidecar.
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
			.PostConfigure(WhatsmeowSidecarOptionsSetup.Apply)
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
		return WhatsmeowSidecar.Create(
			services.GetRequiredService<IOptions<WhatsmeowSidecarOptions>>().Value,
			services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance,
			services.GetService<TimeProvider>(),
			services.GetService<FakeWhatsAppMode>() is not null,
			client =>
			{
				foreach (var handler in handlers)
					handler.AttachTo(client, services);
			}
		);
	}
}
