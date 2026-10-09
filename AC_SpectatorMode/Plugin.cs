using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace AC_SpectatorMode
{
    /// <summary>
    /// Points Aicomi's overworld camera at another character and follows them.
    ///
    /// Loads, binds the settings and starts the per-frame component, which holds the behaviour.
    ///
    /// It works on its own. With AC_MainCameraExtension installed the watched character also gets
    /// that plugin's zoom, first person and lens zoom, because both plugins only change what the
    /// game's own camera reads; neither calls the other.
    /// </summary>
    [BepInProcess(ProcessName)]
    [BepInPlugin(Guid, PluginName, Version)]
    [BepInDependency("AC_MainCameraExtension", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BasePlugin
    {
        public const string PluginName = "AC_SpectatorMode";
        public const string Guid = "AC_SpectatorMode";

        // Keep in step with <Version> in the csproj.
        public const string Version = "1.0.0";

        public const string ProcessName = "Aicomi";

        internal static ManualLogSource Logger = null!;

        public static ConfigEntry<bool> Enabled = null!;
        public static ConfigEntry<bool> ShowName = null!;
        public static ConfigEntry<bool> FollowIntoBuildings = null!;
        public static ConfigEntry<bool> LoadSurroundings = null!;
        public static ConfigEntry<bool> HideSelf = null!;
        public static ConfigEntry<bool> BringReturns = null!;
        public static ConfigEntry<bool> ClothingWithMouse = null!;
        public static ConfigEntry<float> DragSmoothing = null!;
        public static ConfigEntry<float> TapLength = null!;
        public static ConfigEntry<float> DragTurnSpeed = null!;
        public static ConfigEntry<bool> FlyWhenNobody = null!;
        public static ConfigEntry<bool> FlyWhenTheyLeave = null!;
        public static ConfigEntry<float> FlySpeed = null!;
        public static ConfigEntry<float> FlyFast = null!;
        public static ConfigEntry<float> FlySlow = null!;
        public static ConfigEntry<KeyCode> FlyKey = null!;
        public static ConfigEntry<KeyCode> FlyKey2 = null!;
        public static ConfigEntry<KeyCode> ResetKey = null!;
        public static ConfigEntry<KeyCode> ResetKey2 = null!;
        public static ConfigEntry<KeyCode> BringKey = null!;
        public static ConfigEntry<KeyCode> BringKey2 = null!;
        public static ConfigEntry<float> DragPanSpeed = null!;
        public static ConfigEntry<float> DragZoomSpeed = null!;

        // The one switch for diagnostics: extra log lines go behind it.
        public static ConfigEntry<bool> DebugInfo = null!;

        // Every key has an optional second binding.
        public static ConfigEntry<KeyCode> NextKey = null!;
        public static ConfigEntry<KeyCode> NextKey2 = null!;
        public static ConfigEntry<KeyCode> PreviousKey = null!;
        public static ConfigEntry<KeyCode> PreviousKey2 = null!;
        public static ConfigEntry<KeyCode> BackKey = null!;
        public static ConfigEntry<KeyCode> BackKey2 = null!;

        /// <summary>An extra log line, written only while Debug Info is on.</summary>
        internal static void Debug(string text)
        {
            if (DebugInfo.Value) Logger.LogInfo(text);
        }

        /// <summary>
        /// A renamed setting keeps the value it had under its old name. BepInEx keeps values it
        /// no longer has a setting for in a private list; this moves one out of it, once.
        /// </summary>
        private void CarryOver(ConfigEntryBase entry, string oldSection, string oldKey)
        {
            try
            {
                var property = typeof(ConfigFile).GetProperty("OrphanedEntries",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var orphans = property?.GetValue(Config) as System.Collections.Generic.Dictionary<ConfigDefinition, string>;
                ConfigDefinition old = new ConfigDefinition(oldSection, oldKey);
                if (orphans == null || orphans.TryGetValue(old, out string? value) == false) return;

                entry.SetSerializedValue(value);
                orphans.Remove(old);
                Config.Save();
                Log.LogInfo($"Setting \"{oldKey}\" is now \"{entry.Definition.Key}\"; its value ({value}) was carried over.");
            }
            catch (System.Exception e)
            {
                Log.LogWarning($"Could not carry over the old setting \"{oldKey}\": {e.Message}");
            }
        }

        public override void Load()
        {
            Logger = Log;

            // The main switch is always the first thing in the plugin's settings: an empty display
            // category sorts before every named section and is drawn without a heading.
            Enabled = Config.Bind("General", "Enabled", true, new ConfigDescription(
                "Turn the whole plugin on or off.", null,
                new ConfigurationManagerAttributes { Category = "", Order = 1000 }));
            ShowName = Config.Bind("General", "Show Name", true,
                "Shows the name of the character being watched in the corner of the screen.");
            FollowIntoBuildings = Config.Bind("General", "Follow Through Doors", true,
                "When the character being watched goes through a door, the game loads the place they went to. Unticked, the camera moves on to someone else who is still here.");
            LoadSurroundings = Config.Bind("General", "Load Scenery Around Them", true,
                "Loads the part of the map the watched character is in, even when your own character is far away.");
            HideSelf = Config.Bind("General", "Hide My Character", true,
                "While watching someone, your own character is put away where they stand: not shown, not moving, and left alone by others. Going back, or bringing them over, restores them.");
            ClothingWithMouse = Config.Bind("General", "Clothing Menu With Mouse Pointer", true,
                "While watching someone, bringing out the game's mouse pointer (middle click) also opens Clothing State Menu for them, and putting the pointer away closes it. Needs the Clothing State Menu plugin; does nothing without it.");
            BringReturns = Config.Bind("General", "Bring My Character Also Returns", false,
                "After bringing your own character over, the camera goes back to them as well.");
            TapLength = Config.Bind("Mouse", "Tap Length", 0.2f, new ConfigDescription(
                "How long the right button can be held and still count as a tap. A tap switches between watching and the free camera; anything longer is a drag.",
                new AcceptableValueRange<float>(0.05f, 0.5f)));
            DragSmoothing = Config.Bind("Mouse", "Drag Zoom Smoothing", 0.2f, new ConfigDescription(
                "How gently the camera glides when zooming with a mouse drag. 0 moves it at once.",
                new AcceptableValueRange<float>(0f, 1f)));
            FlyKey = Config.Bind("Keys", "Free Camera", KeyCode.Backspace,
                "Switches the free camera on or off. Fly with W A S D, the arrow keys or the left mouse button; E rises, Q sinks; Shift is faster, Ctrl slower. Right click a character to watch them, or a door to go through it.");
            FlyKey2 = Config.Bind("Keys", "Free Camera (second key)", KeyCode.None,
                "An optional second key for the same thing.");
            FlyWhenNobody = Config.Bind("Free Camera", "When Nobody Is Here", true,
                "Asking to watch someone where there is nobody switches to the free camera instead.");
            FlyWhenTheyLeave = Config.Bind("Free Camera", "When They Leave Through A Door", false,
                "With Follow Through Doors off: when the watched character leaves, the camera stays where it is as a free camera, instead of moving on to someone else.");
            FlySpeed = Config.Bind("Free Camera", "Speed", 5f, new ConfigDescription(
                "How fast the free camera flies, in metres a second.",
                new AcceptableValueRange<float>(0.5f, 30f)));
            FlyFast = Config.Bind("Free Camera", "Shift Speed", 3f, new ConfigDescription(
                "How many times faster it flies while Shift is held.",
                new AcceptableValueRange<float>(1f, 10f)));
            FlySlow = Config.Bind("Free Camera", "Ctrl Speed", 0.3f, new ConfigDescription(
                "How much slower it flies while Ctrl is held: 0.3 is about a third of the speed.",
                new AcceptableValueRange<float>(0.05f, 1f)));
            ResetKey = Config.Bind("Keys", "Centre View", KeyCode.Delete,
                "Undoes what the mouse drags moved: the view is centred on the watched character again.");
            ResetKey2 = Config.Bind("Keys", "Centre View (second key)", KeyCode.None,
                "An optional second key for the same thing.");
            BringKey = Config.Bind("Keys", "Bring My Character", KeyCode.End,
                "Moves your own character next to the character being watched, or, from the free camera, to where the camera is.");
            BringKey2 = Config.Bind("Keys", "Bring My Character (second key)", KeyCode.None,
                "An optional second key for the same thing.");
            DragTurnSpeed = Config.Bind("Mouse", "Drag Turn Speed", 3f, new ConfigDescription(
                "While watching someone with the game's mouse pointer out (middle click), dragging with the left button turns the camera. This is how fast.",
                new AcceptableValueRange<float>(0f, 10f)));
            DragPanSpeed = Config.Bind("Mouse", "Drag Pan Speed", 0.03f, new ConfigDescription(
                "While watching someone, holding the right mouse button and dragging up or down moves the camera up or down. This is how fast.",
                new AcceptableValueRange<float>(0f, 0.2f)));
            DragZoomSpeed = Config.Bind("Mouse", "Drag Zoom Speed", 0.15f, new ConfigDescription(
                "While watching someone, holding the right mouse button and dragging left or right zooms. This is how fast.",
                new AcceptableValueRange<float>(0f, 1f)));
            DebugInfo = Config.Bind("Debug", "Debug Info", false,
                "Write extra details to the log, for troubleshooting.");

            // Not F8 (the game's next-period key), Space (the game's first person), F (the free
            // camera in conversations), or F1 / F9 / F11 / F12 (other plugins).
            NextKey = Config.Bind("Keys", "Next Character", KeyCode.PageDown,
                "Watch the next character.");
            NextKey2 = Config.Bind("Keys", "Next Character (second key)", KeyCode.None,
                "An optional second key for the same thing.");
            PreviousKey = Config.Bind("Keys", "Previous Character", KeyCode.PageUp,
                "Watch the previous character.");
            PreviousKey2 = Config.Bind("Keys", "Previous Character (second key)", KeyCode.None,
                "An optional second key for the same thing.");
            BackKey = Config.Bind("Keys", "Back To Player", KeyCode.Home,
                "Stop watching and return the camera to your own character.");
            BackKey2 = Config.Bind("Keys", "Back To Player (second key)", KeyCode.None,
                "An optional second key for the same thing.");

            // BasePlugin.AddComponent registers the type with IL2CPP and puts it on BepInEx's own
            // manager object, which survives every scene change.
            CarryOver(FollowIntoBuildings, "General", "Follow Into Buildings");
            Hooks.InstallInput();
            AddComponent<Spectator>();

            Log.LogInfo($"{PluginName} {Version} loaded");
        }
    }
}
