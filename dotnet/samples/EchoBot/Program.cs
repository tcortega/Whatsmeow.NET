using EchoBot;
using OpenTelemetry;

// Two WhatsApp sessions that echo every text they get. Run it with --Whatsmeow:SidecarPath=<path of whatsmeow-net>
// (the Whatsmeow.Sidecar.Native package puts it next to the app), and scan each QR code with that session's phone.
var builder = Host.CreateApplicationBuilder(args);
_ = builder.Services.AddEchoBot();

// Traces and metrics to an OpenTelemetry collector, by the OTEL_EXPORTER_OTLP_* variables (localhost:4317 by default).
_ = builder
	.Services.AddOpenTelemetry()
	.WithTracing(static tracing => tracing.AddSource(WhatsmeowTelemetry.ActivitySourceName))
	.WithMetrics(static metrics => metrics.AddMeter(WhatsmeowTelemetry.MeterName))
	.UseOtlpExporter();

await builder.Build().RunAsync().ConfigureAwait(false);
