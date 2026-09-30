using System.Text.Json.Nodes;

namespace Moonlace.Core.Penumbra;

/// <summary>
/// Helpers for Penumbra metadata manipulations kept as raw JSON nodes of the
/// shape {"Type": "Eqdp", "Manipulation": {...}}, the way mods store them.
/// Two manipulations with the same identity key target the same game entry
/// (everything but "Entry" matches), so a newer one replaces the older.
/// </summary>
public static class ModManipulations
{
    /// <summary>"Eqdp:Gender=Female;Race=Miqote;SetId=16;Slot=Body": the manipulated target without its value.</summary>
    public static string IdentityKey(JsonObject node)
    {
        var type = node["Type"]?.ToString() ?? "";
        if (node["Manipulation"] is not JsonObject manipulation)
            return type;

        var parts = manipulation
            .Where(kv => kv.Key != "Entry")
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value?.ToJsonString()}");
        return $"{type}:{string.Join(";", parts)}";
    }

    /// <summary>Replaces the manipulation with the same identity in <paramref name="list"/>, or appends it.</summary>
    public static void Upsert(JsonArray list, JsonObject manipulation)
    {
        var key = IdentityKey(manipulation);
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is JsonObject existing && IdentityKey(existing) == key)
            {
                list[i] = manipulation.DeepClone();
                return;
            }
        }

        list.Add(manipulation.DeepClone());
    }
}
