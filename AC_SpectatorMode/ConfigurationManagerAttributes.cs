#nullable disable
using System;

/// <summary>
/// Metadata that BepInEx ConfigurationManager reads by reflection to decide how
/// to present a setting. It deliberately lives in the global namespace with this
/// exact name - ConfigurationManager looks it up by name, not by reference, so
/// every plugin ships its own copy rather than depending on the manager.
///
/// Only the members actually used are kept.
/// </summary>
internal sealed class ConfigurationManagerAttributes
{
	/// <summary>
	/// Higher values sort earlier in the list. Without this, settings appear in
	/// alphabetical order, which scatters related options.
	/// </summary>
	public int? Order;

	/// <summary>
	/// Shows the setting under this heading instead of its section in the file.
	/// An empty string lists it first, above every section, with no heading.
	/// </summary>
	public string Category;
}
