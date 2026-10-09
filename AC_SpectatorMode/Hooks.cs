using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using Commands = AC.KeyBind.Commands;

namespace AC_SpectatorMode
{
    /// <summary>
    /// The few places where watching someone has to change what other code sees.
    ///
    /// 1. The game reads the mouse and keys through its own key-binding layer (ActionMapProvider).
    ///    While someone is being watched, the player's own character must not react to the right
    ///    mouse button: no talking to whoever stands next to them, no walking out of a door.
    /// 2. Clothing State Menu (another plugin, ordinary .NET) asks one method of its own which
    ///    characters are "current". In the overworld the answer is nobody, so its window stays
    ///    empty. While watching, the answer becomes the watched character.
    /// </summary>
    internal static class Hooks
    {
        /// <summary>True while someone is watched and the walking camera is in charge.</summary>
        internal static bool BlockInteraction;

        /// <summary>
        /// True while the game's own mouse pointer is out: left click then belongs to the game's
        /// on-screen buttons and is let through.
        /// </summary>
        internal static bool AllowClick;

        /// <summary>True while the free camera is on: the arrow keys fly it, they do not turn it.</summary>
        internal static bool Flying;

        /// <summary>True while the player's own character is put away.</summary>
        internal static bool HideSelf;

        /// <summary>The character being watched, or null.</summary>
        internal static global::Character.Human? Watched;

        private static Harmony? _harmony;
        private static bool _clothingLooked;
        private static object? _clothingPlugin;
        private static MethodInfo? _clothingRefresh;
        private static PropertyInfo? _clothingShown;

        internal static void InstallInput()
        {
            if (_harmony != null) return;
            _harmony = new Harmony(Plugin.Guid);

            Type provider = typeof(AC.KeyBind.ActionMapProvider);
            Patch(provider, "GetMoveAxis", Type.EmptyTypes, nameof(MoveAxis));
            // Measured: a character who stops for the player goes into the state "Encounter". These
            // four small methods are the ways a character changes state.
            Type npc = typeof(AC.Scene.Explore.NPC);
            Patch(npc, "ChangeState", new[] { typeof(AC.Scene.Explore.NPC.StateID) }, nameof(NoEncounter));
            Patch(npc, "ChangeStateIfDifferent", new[] { typeof(AC.Scene.Explore.NPC.StateID) }, nameof(NoEncounter));
            Patch(npc, "ChangeState", new[] { typeof(int) }, nameof(NoEncounterNumber));
            Patch(npc, "ChangeStateIfDifferent", new[] { typeof(int) }, nameof(NoEncounterNumber));

            // Three small methods with one plain parameter each.
            foreach (string name in new[] { "IsDown", "IsPressed", "IsUp" })
                Patch(provider, name, new[] { typeof(Commands) }, nameof(Command));

            // The game decides every frame whether the player's character is shown. While they
            // are put away, the answer is changed back to "no" right after.
            try
            {
                MethodInfo? refresh = AccessTools.DeclaredMethod(typeof(AC.Scene.Explore.Player), "RefreshVisibility",
                    new[] { typeof(float), typeof(bool) });
                MethodInfo? mine = typeof(Hooks).GetMethod(nameof(KeepSelfHidden), BindingFlags.Static | BindingFlags.NonPublic);
                if (refresh != null && mine != null) _harmony.Patch(refresh, postfix: new HarmonyMethod(mine));
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Could not patch Player.RefreshVisibility: " + e.Message);
            }
        }

        private static void Patch(Type type, string name, Type[] parameters, string prefix)
        {
            try
            {
                MethodInfo? target = AccessTools.DeclaredMethod(type, name, parameters);
                MethodInfo? mine = typeof(Hooks).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic);
                if (target == null || mine == null)
                {
                    Plugin.Logger.LogWarning($"{type.Name}.{name} not found.");
                    return;
                }
                _harmony!.Patch(target, prefix: new HarmonyMethod(mine));
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Could not patch {type.Name}.{name}: {e.Message}");
            }
        }

        private static void KeepSelfHidden(AC.Scene.Explore.Player __instance)
        {
            if (HideSelf == false || __instance == null) return;
            try
            {
                var grid = __instance.CharacterGrid;
                if (grid != null && grid.Visible) grid.VisibleImmediate = false;
            }
            catch
            {
                // Never disturb the game.
            }
        }

        // The game's own camera shortcuts. While flying, the keys they sit on fly the camera
        // (arrows, Q, E, Ctrl, Space), so the game does not get to act on them as well.
        private static bool IsCameraKey(Commands command)
        {
            switch (command)
            {
                case Commands.CameraUp:
                case Commands.CameraDown:
                case Commands.CameraLeft:
                case Commands.CameraRight:
                case Commands.CameraSwitch:
                case Commands.CameraTurnOver:
                case Commands.DutchPositive:
                case Commands.DutchNegative:
                case Commands.FovPositive:
                case Commands.FovNegative:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsMovement(Commands command)
        {
            switch (command)
            {
                case Commands.ForwardPriority:
                case Commands.Forward:
                case Commands.Backward:
                case Commands.Left:
                case Commands.Right:
                case Commands.WalkShift:
                case Commands.CrouchShift:
                case Commands.MoveAxis:
                    return true;
                default:
                    return false;
            }
        }

        // While the player's character is put away, nobody stops for them or waits to be talked to.
        private static bool NoEncounter(AC.Scene.Explore.NPC.StateID stateID)
        {
            return NoEncounterNumber((int)stateID);
        }

        private static bool NoEncounterNumber(int stateID)
        {
            if (HideSelf == false) return true;
            // Following is refused too: a character who set off after the hidden player ran on the
            // spot above where they had been put.
            return stateID != (int)AC.Scene.Explore.NPC.StateID.Encounter
                && stateID != (int)AC.Scene.Explore.NPC.StateID.Wait2TalkFromPlayer
                && stateID != (int)AC.Scene.Explore.NPC.StateID.Follow;
        }

        private static bool MoveAxis(ref Vector2 __result)
        {
            if (HideSelf == false) return true;
            __result = Vector2.zero;
            return false;
        }

        private static bool Command(Commands command, ref bool __result)
        {
            if (Flying && IsCameraKey(command))
            {
                __result = false;
                return false;
            }

            // Put away, the player's character does not walk.
            if (HideSelf && IsMovement(command))
            {
                __result = false;
                return false;
            }

            if (BlockInteraction == false) return true;
            if (command != Commands.Access && command != Commands.Cancel && (command != Commands.Click || AllowClick)) return true;
            __result = false;
            return false;
        }

        // ---------------------------------------------------------------------------------------
        // Clothing State Menu
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Called once the game is running, so every plugin has loaded. Does nothing if Clothing
        /// State Menu is not installed.
        /// </summary>
        internal static void InstallClothingMenu()
        {
            if (_clothingLooked) return;
            _clothingLooked = true;
            try
            {
                foreach (BepInEx.PluginInfo info in IL2CPPChainloader.Instance.Plugins.Values)
                {
                    object? instance = info.Instance;
                    if (instance == null || instance.GetType().FullName != "ClothingStateMenu.ClothingStateMenuPlugin") continue;

                    Type? utilities = instance.GetType().Assembly.GetType("IllusionMods.GameUtilities");
                    MethodInfo? current = utilities?.GetMethod("GetCurrentHumans",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    MethodInfo? mine = typeof(Hooks).GetMethod(nameof(CurrentHumans), BindingFlags.Static | BindingFlags.NonPublic);
                    if (current == null || mine == null)
                    {
                        Plugin.Logger.LogWarning("Clothing State Menu is installed but looks different from the version this was written for; it will not follow the watched character.");
                        return;
                    }

                    _harmony ??= new Harmony(Plugin.Guid);
                    _harmony.Patch(current, postfix: new HarmonyMethod(mine));
                    _clothingPlugin = instance;
                    _clothingRefresh = instance.GetType().GetMethod("RefreshCharacterList",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _clothingShown = instance.GetType().GetProperty("ShowInterface",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    Plugin.Logger.LogInfo("Clothing State Menu found: it will show the character being watched.");
                    return;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Could not connect to Clothing State Menu: " + e.Message);
            }
        }

        // Only steps in where the menu would otherwise have nobody: the maker, H scenes and
        // conversations keep their own answer.
        private static void CurrentHumans(ref IEnumerable<global::Character.Human> __result)
        {
            try
            {
                global::Character.Human? watched = Watched;
                if (watched == null) return;
                if (__result != null && __result.Any()) return;
                __result = new[] { watched };
            }
            catch
            {
                // Leave the menu's own answer alone.
            }
        }

        /// <summary>Opens or closes Clothing State Menu's window, if that plugin is installed.</summary>
        internal static void ShowClothingMenu(bool shown)
        {
            if (_clothingPlugin == null || _clothingShown == null) return;
            try { _clothingShown.SetValue(_clothingPlugin, shown); }
            catch (Exception e) { Plugin.Debug("[spectate] opening Clothing State Menu failed: " + e.Message); }
        }

        /// <summary>Tells the menu to look again, after the watched character changed.</summary>
        internal static void RefreshClothingMenu()
        {
            if (_clothingPlugin == null || _clothingRefresh == null) return;
            try { _clothingRefresh.Invoke(_clothingPlugin, null); }
            catch (Exception e) { Plugin.Debug("[spectate] refreshing Clothing State Menu failed: " + e.Message); }
        }
    }
}
