# Options

Read this when adding or changing configuration in `Whatsmeow.Client` or `Whatsmeow.Extensions.Hosting`.
This is the house ASP.NET Core options guide adapted to a library that works both with DI and without it (ARCHITECTURE §8.2).
Type names below are indicative until the code exists.

Use the options pattern.
Do not walk the tree, deserialize a file, overlay env vars by hand, and `AddSingleton` the result.

## Two entry points, one validator

- Without DI: `WhatsmeowSidecar.StartAsync(options, ct)` takes a `WhatsmeowSidecarOptions` instance.
- With DI: `services.AddWhatsmeow(...)` (namespace `Whatsmeow.Extensions.Hosting`) binds the same type from `IConfiguration`.

Both paths run the same setup step and the same generated validator before the sidecar starts.
A bad option fails at start on both paths, never on the first send.

## Bind

`IConfiguration` is the source on the DI path.
`appsettings.json`, environment variables, and test settings all feed the same tree under the `Whatsmeow` section.
Do not read `Environment.GetEnvironmentVariable` at a call site.

```csharp
services.AddOptions<WhatsmeowSidecarOptions>()
	.BindConfiguration("Whatsmeow")
	.PostConfigure(WhatsmeowSidecarOptionsSetup.Apply)
	.ValidateOnStart();
services.AddSingleton<IValidateOptions<WhatsmeowSidecarOptions>, WhatsmeowSidecarOptionsValidator>();
```

`BindConfiguration` is the bind.
Under AOT it must go through the configuration-binding source generator, not the reflection binder.
A library does not get that generator from `IsAotCompatible`: set `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>` in `Whatsmeow.Extensions.Hosting`, or `BindConfiguration` raises IL2026/IL3050 [observed: ARCHITECTURE §14 Spike results].
Path resolve, computed defaults, and the `WHATSMEOW_NET_SIDECAR_PATH` override belong in the one setup step (`WhatsmeowSidecarOptionsSetup.Apply`), not in a private `Load` and not in the binary locator.
The no-DI path calls the same `Apply`.
That is the only place the environment variable is read.

### Time options are `TimeSpan`

Time-valued options are `TimeSpan` (see [csharp.md](csharp.md) Time).
The binding generator binds `TimeSpan` from a `00:00:30` string under Native AOT [observed: ARCHITECTURE §14 Spike results].
Do not invent a custom time type for options: the spike showed that a type the binder does not know (NodaTime `Duration`) is silently left at its default, with no error.

## Fail at start

`ValidateOnStart` throws `OptionsValidationException` at host start, not on the first request.
The validator is generated: `[OptionsValidator]` on a `partial class` implementing `IValidateOptions<T>`.
`ValidateDataAnnotations()` reflects, so it is banned (see [csharp.md](csharp.md) Reflection).

```csharp
[OptionsValidator]
public sealed partial class WhatsmeowSidecarOptionsValidator : IValidateOptions<WhatsmeowSidecarOptions>;
```

`[Range]` and `[Required]` on the options type feed the generator, which is fine for a small closed set.
`[Range(typeof(TimeSpan), ...)]` is not: its constructor is trim-unsafe, and it fails a library build with IL2026 [observed: #4, SDK 10.0.403], so a `TimeSpan` bound is a hand-written rule.
Today every rule on `WhatsmeowSidecarOptions` is hand-written (`WhatsmeowSidecarOptionsSetup.Validate`), so there is no generated validator yet; add one with the first rule an attribute can express.
When a rule needs the environment, a file check, or more than one failure string, hand-write the extra `IValidateOptions<T>` next to it.

## Call sites

Library types take `IOptions<WhatsmeowSidecarOptions>`.
Use `IOptionsMonitor<T>` only when the consumer must see a reload.
Do not `AddSingleton` the bound object.
Do not inject `WhatsmeowSidecarOptions` directly once options exist.
Options types are classes with `{ get; set; }`.
Binding and setup assign properties.
Records we own still never use primary constructors.

## Tests

The fixture sets options through configuration or the options instance.
Do not special-case tests by reading env after bind.

## Do not

* Keep a second `WhatsmeowSidecarOptions.Load` next to `IConfiguration`.
* Validate lazily in a call when `ValidateOnStart` would have caught it.
* Register DI extensions in `Microsoft.Extensions.DependencyInjection`. They live in `Whatsmeow.Extensions.Hosting`.
