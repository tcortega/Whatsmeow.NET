using System.Globalization;

namespace Whatsmeow;

/// <summary>
/// A WhatsApp address, in whatsmeow's form <c>user[.agent][:device]@server</c>: a user by phone number
/// (<c>@s.whatsapp.net</c>) or by LID (<c>@lid</c>), a group (<c>@g.us</c>), or a server alone. A JID always has a
/// server; <c>default(Jid)</c> has none, prints as an empty string, and equals no parsed JID.
/// </summary>
public readonly struct Jid : IEquatable<Jid>, ISpanParsable<Jid>
{
	/// <summary>The user part; empty for a server alone.</summary>
	public required string User
	{
		get => field ?? "";
		init;
	}

	/// <summary>The agent, 0 for none.</summary>
	public byte Agent { get; init; }

	/// <summary>The device, 0 for the primary device or none.</summary>
	public ushort Device { get; init; }

	/// <summary>The server; empty only in <c>default(Jid)</c>, which is not a JID.</summary>
	public required string Server
	{
		get => field ?? "";
		init;
	}

	/// <summary>Whether this is a user addressed by phone number (<c>@s.whatsapp.net</c>).</summary>
	public bool IsPhoneNumber => string.Equals(Server, PhoneNumberServer, StringComparison.Ordinal);

	/// <summary>
	/// Whether this is a user addressed by LID (<c>@lid</c>), WhatsApp's address that hides the phone number. The other
	/// form of the same user, when known, comes beside it: an event's <c>SenderAlt</c>, or
	/// <see cref="IWhatsAppClient.ResolveJidsAsync"/>.
	/// </summary>
	public bool IsLid => string.Equals(Server, LidServer, StringComparison.Ordinal);

	private const string PhoneNumberServer = "s.whatsapp.net";
	private const string LidServer = "lid";

	/// <summary>Parses whatsmeow's form.</summary>
	/// <exception cref="WhatsmeowValidationException">
	/// <paramref name="s"/> is not a JID. The message leaves <paramref name="s"/> out: it can hold a phone number, and an
	/// exception's message reaches logs (ARCHITECTURE §8.8).
	/// </exception>
	public static Jid Parse(string s)
	{
		ArgumentNullException.ThrowIfNull(s);
		return Parse(s.AsSpan());
	}

	/// <inheritdoc cref="Parse(string)"/>
	public static Jid Parse(ReadOnlySpan<char> s)
	{
		if (!TryParse(s, out var jid))
			WhatsmeowValidationException.ThrowInvalid("The text is not a JID: user[.agent][:device]@server.");
		return jid;
	}

	/// <summary>Parses whatsmeow's form; false when <paramref name="s"/> is not a JID.</summary>
	public static bool TryParse([NotNullWhen(true)] string? s, out Jid result) => TryParse(s.AsSpan(), out result);

	/// <inheritdoc cref="TryParse(string?, out Jid)"/>
	public static bool TryParse(ReadOnlySpan<char> s, out Jid result)
	{
		result = default;
		if (s.IsEmpty)
			return false;

		var at = s.IndexOf('@');
		if (at < 0)
		{
			result = new Jid { User = "", Server = s.ToString() };
			return true;
		}

		var server = s[(at + 1)..];
		if (server.IsEmpty || server.Contains('@'))
			return false;

		var user = s[..at];
		byte agent = 0;
		ushort device = 0;
		var dot = user.IndexOf('.');
		if (dot >= 0)
		{
			// user.agent[:device]
			var ad = user[(dot + 1)..];
			user = user[..dot];
			var colon = ad.IndexOf(':');
			if (!byte.TryParse(colon < 0 ? ad : ad[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out agent))
				return false;
			if (colon >= 0 && !ushort.TryParse(ad[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out device))
				return false;
			return TryCreate(user, agent, device, server, out result);
		}

		var deviceColon = user.IndexOf(':');
		if (deviceColon >= 0)
		{
			// user:device
			if (!ushort.TryParse(user[(deviceColon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out device))
				return false;
			user = user[..deviceColon];
		}

		return TryCreate(user, agent, device, server, out result);
	}

	static Jid IParsable<Jid>.Parse(string s, IFormatProvider? provider) => Parse(s);

	static bool IParsable<Jid>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Jid result) => TryParse(s, out result);

	static Jid ISpanParsable<Jid>.Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s);

	static bool ISpanParsable<Jid>.TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Jid result) => TryParse(s, out result);

	/// <summary>whatsmeow's form, which <see cref="Parse(string)"/> reads back; empty for <c>default(Jid)</c>.</summary>
	public override string ToString() =>
		(Server.Length, Agent, Device, User.Length) switch
		{
			(0, _, _, _) => "",
			(_, > 0, _, _) => string.Create(CultureInfo.InvariantCulture, $"{User}.{Agent}:{Device}@{Server}"),
			(_, _, > 0, _) => string.Create(CultureInfo.InvariantCulture, $"{User}:{Device}@{Server}"),
			(_, _, _, > 0) => $"{User}@{Server}",
			_ => Server,
		};

	/// <inheritdoc/>
	public bool Equals(Jid other) =>
		string.Equals(User, other.User, StringComparison.Ordinal)
		&& Agent == other.Agent
		&& Device == other.Device
		&& string.Equals(Server, other.Server, StringComparison.Ordinal);

	/// <inheritdoc/>
	public override bool Equals(object? obj) => obj is Jid other && Equals(other);

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(User), Agent, Device, StringComparer.Ordinal.GetHashCode(Server));

	/// <summary>Whether two JIDs are the same address.</summary>
	public static bool operator ==(Jid left, Jid right) => left.Equals(right);

	/// <summary>Whether two JIDs are different addresses.</summary>
	public static bool operator !=(Jid left, Jid right) => !left.Equals(right);

	private static bool TryCreate(ReadOnlySpan<char> user, byte agent, ushort device, ReadOnlySpan<char> server, out Jid result)
	{
		result = default;
		if (user.ContainsAny('.', ':'))
			return false;
		result = new Jid { User = user.ToString(), Agent = agent, Device = device, Server = server.ToString() };
		return true;
	}
}
