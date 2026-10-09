using System;
using System.Collections.Generic;
using AC.Scene;
using AC.Scene.Explore;
using UnityEngine;

namespace AC_SpectatorMode
{
    /// <summary>
    /// The free camera: the same game camera, following nobody.
    ///
    /// Watching someone is pointing the game's camera at a point on their body. Flying is pointing
    /// it at a point of this plugin's own that hangs in the air and is moved with the keys, with
    /// the game in its own first-person mode: there the camera sits on the point it follows and
    /// turns on the spot, instead of circling it. The game still turns the camera with the mouse,
    /// loads the scenery it is asked for and draws the minimap, and AC_MainCameraExtension's wheel
    /// narrows the lens there. All this file does is move the point, and decide what the camera
    /// is aimed at.
    ///
    /// The player's own character is put away for as long as it lasts, exactly as while watching.
    /// </summary>
    public partial class Spectator
    {
        // True while the camera follows the fly point instead of a character.
        private bool _flying;
        // This plugin's own object in the air; the camera follows it.
        private Transform? _flyPoint;
        private Vector3 _flyVelocity;
        // Height still to be travelled after a right-button drag, so the lift glides.
        private Vector3 _flyNudgeLeft;
        // The piece of map the camera is over, and since when: asked for only once it is steady.
        private float _areaSince = -100f;
        // Double-tapping forward sprints for as long as forward stays held.
        private float _lastForwardTap = -100f;
        private bool _sprinting;
        // Set by a two-button drag: the left button does not fly forward again until it has been
        // let go, however the two buttons are released.
        private bool _leftSpent;

        // The game's own distance within which icons over doors and people are shown, measured
        // from the player's character. Raised while a door is aimed at, put back after.
        private float _iconDistanceBefore;
        private bool _iconDistanceRaised;

        // Door names put into the player's language, looked up once each.
        private readonly Dictionary<string, string> _doorNames = new Dictionary<string, string>();
        private static System.Reflection.MethodInfo? _translate;
        private static object? _translator;
        private static bool _translateLooked;

        // True while other doors' icons have been made see-through so only the aimed one shows.
        private bool _iconsDimmed;
        // Every icon the game has (doors and people), looked up now and then.
        private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<AC.Scene.Explore.UI.EventIcon>? _icons;
        private float _iconsAt = -100f;

        // The camera mode from before, put back when the free camera hands back to the player.
        private TrackingCamera.States _flyStateBefore = TrackingCamera.States.TPS;

        // The map a door is taking the free camera to, or -1.
        private int _flyDestination = -1;

        private float _lastAreaProbe = -100f;
        private int _lastAreaFound = -2;

        // What the camera is aimed at right now: a character to watch, or a door to go through.
        private Actor? _hoverActor;
        private AC.Scene.Explore.Gateway? _hoverDoor;
        private string _hoverText = "";
        private float _lastHover = -100f;

        // Every door in the world, looked up now and then rather than every frame.
        private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<AC.Scene.Explore.Gateway>? _doors;
        private float _doorsAt = -100f;

        // How high above the floor the fly point arrives after a door; the camera sits a little
        // above the point.
        private const float ArrivalHeight = 1.1f;

        // Where the camera was a moment ago, while the watched character was still on this map.
        // A character who goes through a door is moved somewhere meaningless, camera and all,
        // before this plugin can notice; the free camera then starts from here instead.
        private Vector3 _eyeOld;
        private Vector3 _eyeNew;
        private float _eyeStamp = -100f;
        private int _eyeCount;

        private void RememberEye(Vector3 eye)
        {
            if (Time.unscaledTime - _eyeStamp < 0.5f) return;
            _eyeStamp = Time.unscaledTime;
            _eyeOld = _eyeNew;
            _eyeNew = eye;
            _eyeCount++;
        }

        private Transform FlyPoint()
        {
            if (_flyPoint != null) return _flyPoint;

            GameObject made = new GameObject("SpectatorFlyPoint");
            DontDestroyOnLoad(made);
            _flyPoint = made.transform;
            return _flyPoint;
        }

        // In first person the game puts the camera this far above the point it follows.
        private static float FlyArm(TrackingCamera tracking)
        {
            return tracking._fpsComponent != null ? tracking._fpsComponent.VerticalArmLength : 0.3f;
        }

        /// <summary>
        /// Starts the free camera where the camera is right now, or, when the watched character
        /// has just left the map, where it was a moment before they did.
        /// </summary>
        private void StartFlying(ExploreScene explore, TrackingCamera tracking, bool whereTheyWere = false)
        {
            Camera camera = explore._camera;
            Player player = explore.Player;
            if (camera == null || player == null) return;

            // Where the view is now, before anything is let go of.
            Vector3 eye = whereTheyWere && _eyeCount >= 2 ? _eyeOld : camera.transform.position;

            // Let go of whoever was being watched, without bringing the player back.
            Unwatch();

            // The player's own character must not walk while the same keys fly the camera, so
            // they are put away whatever the "Hide My Character" setting says.
            PutSelfAway(player, true);

            Transform point = FlyPoint();
            point.position = eye - Vector3.up * FlyArm(tracking);
            point.rotation = Quaternion.identity;
            _flyVelocity = Vector3.zero;
            _flyDestination = -1;

            tracking.Follow = point;

            if (_flying == false) _flyStateBefore = tracking.State;
            if (tracking.State != TrackingCamera.States.FPS) tracking.State = TrackingCamera.States.FPS;

            _flying = true;
            Hooks.Flying = true;

            Note("Free camera");
            Plugin.Debug($"[spectate] free camera on at {point.position}; the camera mode was {_flyStateBefore}");
        }

        /// <summary>
        /// Ends the free camera's own state. Who the camera follows next is the caller's business:
        /// a character (Watch), who is always seen from outside, or the player (Stop), who gets
        /// the camera mode they had before.
        /// </summary>
        private void EndFlying(TrackingCamera? tracking, bool toCharacter = false)
        {
            if (_flying == false) return;
            _flying = false;
            Hooks.Flying = false;
            _flyVelocity = Vector3.zero;
            _flyNudgeLeft = Vector3.zero;
            _sprinting = false;
            _flyDestination = -1;
            ClearHover();

            if (tracking != null)
            {
                TrackingCamera.States next = toCharacter ? TrackingCamera.States.TPS : _flyStateBefore;
                if (next != TrackingCamera.States.FPS) next = TrackingCamera.States.TPS;
                if (tracking.State != next) tracking.State = next;
            }
            Plugin.Debug("[spectate] free camera off");
        }

        /// <summary>
        /// Puts the fly point at head height on the player's character and turns the camera the
        /// way they face: into the room they were put down in, not back at the door.
        /// </summary>
        private void SitOn(TrackingCamera tracking, Player player, Transform point)
        {
            Transform body = player.Transform;
            if (body == null) return;
            point.position = body.position + Vector3.up * ArrivalHeight;

            TrackingCamera.AngleParameter orbit = tracking._parameter;
            orbit.EulerAngles = new Vector2(0f, body.eulerAngles.y);
            tracking._parameter = orbit;
            _turnAngles = orbit.EulerAngles;
            _turnHeld = true;
            TurnAgain();
            _turnHeld = false;
        }

        /// <summary>
        /// "Door to the cafe". The door objects are named link_03_カフェ: the place is the last
        /// part, in Japanese, put through the translation plugin when there is one.
        /// </summary>
        private string DoorName(AC.Scene.Explore.Gateway door)
        {
            string raw = Safe(() => door.name, "");
            if (_doorNames.TryGetValue(raw, out string? known)) return known;

            string place = raw;
            if (place.StartsWith("link_")) place = place.Substring(5);
            int cut = place.IndexOf('_');
            if (cut >= 0 && cut < 3) place = place.Substring(cut + 1);

            string shown = place.Length == 0 ? "Door" : "Door to " + PlaceName(place);
            _doorNames[raw] = shown;
            return shown;
        }

        // The places the test map has, as the game's files name them. Anything not listed goes to
        // the translation plugin, and failing that is shown as it is.
        private static string PlaceName(string place)
        {
            if (place.Contains("屋外")) return "outside";
            if (place.Contains("トイレ")) return "the toilets";
            if (place.Contains("カフェ")) return "the cafe";
            if (place.Contains("図書館")) return "the library";
            if (place.Contains("銭湯")) return "the public bath";
            if (place.Contains("更衣室")) return "the changing room";
            if (place.Contains("教室")) return "the classroom";
            if (place.Contains("廃墟")) return "the ruins";
            return Translated(place);
        }

        // XUnity.AutoTranslator, if installed, found by name so that nothing depends on it.
        private static string Translated(string text)
        {
            try
            {
                if (_translateLooked == false)
                {
                    _translateLooked = true;
                    foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type? type = assembly.GetType("XUnity.AutoTranslator.Plugin.Core.AutoTranslator");
                        if (type == null) continue;
                        _translator = type.GetProperty("Default")?.GetValue(null);
                        _translate = _translator?.GetType().GetMethod("TryTranslate", new[] { typeof(string), typeof(string).MakeByRefType() });
                        break;
                    }
                }
                if (_translator == null || _translate == null) return text;

                object?[] arguments = { text, null };
                bool ok = (bool)_translate.Invoke(_translator, arguments)!;
                string? result = arguments[1] as string;
                return ok && string.IsNullOrWhiteSpace(result) == false ? result! : text;
            }
            catch
            {
                return text;
            }
        }

        /// <summary>Forgets the watched character but stays away from the player.</summary>
        private void Unwatch()
        {
            if (_target == null) return;

            DropAnchor();
            if (_stats != null)
            {
                try { _stats.Target = null; } catch { }
            }
            bool was = Hooks.Watched != null;
            Hooks.Watched = null;
            if (was) Hooks.RefreshClothingMenu();

            _target = null;
            _targetLook = null;
            _targetName = "";
            _shift = Vector3.zero;
            _shiftWanted = Vector3.zero;
            _distEasing = false;
            _leftMap = false;
        }

        /// <summary>The free camera gives way when the game needs its camera, like watching does.</summary>
        private static string? WhyStopFlying(ExploreScene explore, TrackingCamera tracking)
        {
            TrackingCamera.States state = tracking.State;
            if (state != TrackingCamera.States.TPS && state != TrackingCamera.States.FPS)
                return "the game took the camera (mode " + state + ")";

            Camera camera = explore._camera;
            if (camera == null || camera.enabled == false) return "a conversation started";

            return null;
        }

        /// <summary>Every frame while flying.</summary>
        private void Fly(ExploreScene explore, TrackingCamera tracking)
        {
            Transform point = FlyPoint();
            Transform now = tracking.Follow;
            if (now == null || now.Pointer != point.Pointer) tracking.Follow = point;

            bool free = CanSwitch(explore, tracking);
            bool travelling = Time.unscaledTime < _travelUntil;

            // Through a door: the wait ends as soon as the game says the new map is in.
            if (travelling && _flyDestination >= 0 && Time.unscaledTime - _lastTravel > 1f && free
                && Safe(() => explore.Player.MapID, -1) == _flyDestination
                && Safe(() => global::Manager.Scene.IsNowLoading, false) == false
                && Safe(() => global::Manager.Scene.IsNowLoadingFade, false) == false)
            {
                _travelUntil = Time.unscaledTime;
                travelling = false;
            }

            if (travelling)
            {
                // From the moment the game has the player's character on the new map, sit on them
                // and look their way, so the picture is already right when the fade lifts.
                Player moved = explore.Player;
                if (_flyDestination >= 0 && moved != null && Safe(() => moved.MapID, -1) == _flyDestination)
                    SitOn(tracking, moved, point);
                return;
            }

            if (AfterTravel(explore, free))
            {
                // The game has put the player's character at the door of the new map: start there.
                Player arrived = explore.Player;
                if (arrived != null) SitOn(tracking, arrived, point);
                else point.position = _selfHome + Vector3.up * ArrivalHeight;
                _flyVelocity = Vector3.zero;
                _flyDestination = -1;
                Plugin.Debug($"[spectate] free camera arrived at {point.position}");
            }

            // A map load, or anything else, may have put the game back in its circling camera.
            if (tracking.State == TrackingCamera.States.TPS) tracking.State = TrackingCamera.States.FPS;

            if (free == false && _mouseMode == false)
            {
                _flyVelocity = Vector3.zero;
                return;
            }

            Move(explore, point);

            if (Plugin.LoadSurroundings.Value) LoadAreaUnder(point.position);

            Hover(explore);
        }

        /// <summary>
        /// W A S D or the arrow keys, in the direction the camera looks; E up, Q down; holding the
        /// left mouse button also flies forward. Shift is faster, Ctrl slower. The game's own use
        /// of those keys is switched off while flying (Hooks.cs).
        /// </summary>
        private void Move(ExploreScene explore, Transform point)
        {
            Transform view = explore._camera.transform;

            // With the game's pointer out, the left button turns the camera instead.
            bool pointerLocked = Cursor.lockState == CursorLockMode.Locked;

            float ahead = 0f;
            float aside = 0f;
            float lift = 0f;
            // Left button alone flies forward; with the right button held it belongs to the drag.
            if (Input.GetMouseButton(0) == false) _leftSpent = false;
            else if (Input.GetMouseButton(1)) _leftSpent = true;
            bool leftAlone = pointerLocked && Input.GetMouseButton(0) && Input.GetMouseButton(1) == false && _leftSpent == false;
            bool forward = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) || leftAlone;
            if (forward) ahead += 1f;

            // Two presses of forward within a moment: sprint until forward is let go.
            if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow)
                || (pointerLocked && Input.GetMouseButtonDown(0) && Input.GetMouseButton(1) == false))
            {
                if (Time.unscaledTime - _lastForwardTap < 0.3f) _sprinting = true;
                _lastForwardTap = Time.unscaledTime;
            }
            if (forward == false) _sprinting = false;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) ahead -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) aside += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) aside -= 1f;
            if (Input.GetKey(KeyCode.E)) lift += 1f;
            if (Input.GetKey(KeyCode.Q)) lift -= 1f;

            Vector3 wanted = view.forward * ahead + view.right * aside + Vector3.up * lift;
            if (wanted.sqrMagnitude > 1f) wanted.Normalize();

            float speed = Plugin.FlySpeed.Value;
            if (_sprinting || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) speed *= Plugin.FlyFast.Value;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) speed *= Plugin.FlySlow.Value;
            wanted *= speed;

            // A short ease in and out, so starting and stopping are not abrupt.
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            _flyVelocity = Vector3.Lerp(_flyVelocity, wanted, 1f - Mathf.Exp(-dt / 0.08f));
            if (_flyVelocity.sqrMagnitude < 0.0001f && wanted == Vector3.zero) _flyVelocity = Vector3.zero;

            point.position += _flyVelocity * dt;

            // What the right-button drags asked for, eased the same way as the drags while watching.
            if (_flyNudgeLeft != Vector3.zero)
            {
                float smoothing = Plugin.DragSmoothing.Value;
                float step = smoothing <= 0.001f ? 1f : 1f - Mathf.Exp(-dt / smoothing);
                Vector3 move = _flyNudgeLeft.sqrMagnitude < 0.000004f ? _flyNudgeLeft : _flyNudgeLeft * step;
                point.position += move;
                _flyNudgeLeft -= move;
            }
        }

        /// <summary>
        /// The right-button drags: up and down lifts or sinks the camera. Left and right is left
        /// to AC_MainCameraExtension, which zooms the lens with it as in first person.
        /// With the left button as well: forward and back moves the camera along its view, left
        /// and right slides it sideways. A short press without dragging is a tap instead.
        /// </summary>
        private void FlyDrag(ExploreScene explore, TrackingCamera tracking)
        {
            float across = Input.GetAxis("Mouse X");
            float up = Input.GetAxis("Mouse Y");
            if (across == 0f && up == 0f) return;

            float pan = Plugin.DragPanSpeed.Value;
            if (Input.GetMouseButton(0))
            {
                Transform view = explore._camera.transform;
                _flyNudgeLeft += view.forward * (up * pan) + view.right * (across * pan);
                return;
            }

            _flyNudgeLeft += Vector3.up * (up * pan);
        }

        /// <summary>
        /// What the camera is aimed at: the nearest character or door its line of sight passes
        /// close to. With the game's pointer out, the line goes through the pointer instead of the
        /// middle of the screen.
        /// </summary>
        private void FindAimed(ExploreScene explore, bool log, out Actor? actor, out AC.Scene.Explore.Gateway? door)
        {
            actor = null;
            door = null;

            Camera camera = explore._camera;
            bool pointerOut = Cursor.lockState != CursorLockMode.Locked;
            Ray ray = pointerOut
                ? camera.ScreenPointToRay(Input.mousePosition)
                : new Ray(camera.transform.position, camera.transform.forward);

            float actorAt = float.MaxValue;
            List<Actor> candidates = Candidates(explore, null);
            for (int i = 0; i < candidates.Count; i++)
            {
                Actor candidate = candidates[i];
                Transform? look = Safe(() => LookOf(candidate), null);
                if (look == null) continue;

                // The neck is where the camera would follow; aim a little lower, at the chest.
                Vector3 chest = look.position + Vector3.down * 0.25f;
                float along = Along(ray, chest);
                if (along < 0.2f || along > 80f || along >= actorAt) continue;
                if (Off(ray, chest, along) > 0.45f + 0.02f * along) continue;

                actorAt = along;
                actor = candidate;
            }

            if (_doors == null || Time.unscaledTime - _doorsAt > 2f)
            {
                _doors = UnityEngine.Object.FindObjectsOfType<AC.Scene.Explore.Gateway>();
                _doorsAt = Time.unscaledTime;
            }

            float doorAt = float.MaxValue;
            for (int i = 0; i < _doors.Length; i++)
            {
                AC.Scene.Explore.Gateway gateway = _doors[i];
                if (gateway == null) continue;

                Vector3 middle = gateway.transform.position + Vector3.up;
                float along = Along(ray, middle);
                if (along < 0.2f || along > 20f) continue;
                float off = Off(ray, middle, along);
                if (log) Plugin.Debug($"[spectate] door \"{gateway.name}\" to {Safe(() => gateway.DestinationID, -1)}: {along:F1} m along, {off:F1} m off the line");

                if (off > 1.2f || along >= doorAt) continue;

                // Not through walls. The buildings hold several rooms side by side (three toilets,
                // several classrooms), each with its own door; without this the doors of the rooms
                // next door could be aimed at and lit up too.
                Vector3 toDoor = middle - ray.origin;
                float reach = toDoor.magnitude - 0.8f;
                if (reach > 0f && Physics.Raycast(ray.origin, toDoor.normalized, reach,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) continue;
                doorAt = along;
                door = gateway;
            }

            // One thing at a time: whichever is nearer.
            if (actor != null && door != null)
            {
                if (actorAt <= doorAt) door = null;
                else actor = null;
            }
        }

        /// <summary>A right click acts on what the camera is aimed at.</summary>
        private void Aim(ExploreScene explore, TrackingCamera tracking)
        {
            FindAimed(explore, Plugin.DebugInfo.Value, out Actor? actor, out AC.Scene.Explore.Gateway? door);

            if (actor != null)
            {
                Plugin.Debug($"[spectate] free camera: right click on {NameOf(actor)}");
                Watch(tracking, actor);
                return;
            }

            if (door == null) return;

            int destination = Safe(() => door.DestinationID, -1);
            int here = Safe(() => explore.Player.MapID, -1);
            if (destination < 0 || destination == here)
            {
                Note("That door leads nowhere");
                Plugin.Debug($"[spectate] door \"{door.name}\" has destination {destination}; the game is on map {here}");
                return;
            }

            Note("Going through the door");
            Plugin.Debug($"[spectate] door \"{door.name}\": UniqueID {Safe(() => door.UniqueID, -1)}, RuntimeUniqueID {Safe(() => door.RuntimeUniqueID, -1)}, at {door.transform.position}, destination {destination}");
            ClearHover();
            _flyDestination = destination;
            Travel(explore, destination, here);
        }

        /// <summary>
        /// A few times a second: note what the camera is aimed at, so the corner text, the game's
        /// character panel and the door's own icon can show it before any click.
        /// </summary>
        private void Hover(ExploreScene explore)
        {
            if (Time.unscaledTime - _lastHover < 0.08f) return;
            _lastHover = Time.unscaledTime;

            FindAimed(explore, false, out Actor? actor, out AC.Scene.Explore.Gateway? door);
            _hoverActor = actor;

            IntPtr was = _hoverDoor != null ? _hoverDoor.Pointer : IntPtr.Zero;
            IntPtr now = door != null ? door.Pointer : IntPtr.Zero;
            if (was != now)
            {
                if (_hoverDoor != null) DoorSelected(_hoverDoor, false);
                _hoverDoor = door;
                if (door != null) DoorSelected(door, true);
            }
            _hoverText = actor != null ? NameOf(actor) + "   (right click to watch)"
                : door != null ? DoorName(door) + "   (right click to go through)"
                : "";
        }

        private void ClearHover()
        {
            if (_hoverDoor != null) DoorSelected(_hoverDoor, false);
            _hoverDoor = null;
            _hoverActor = null;
            _hoverText = "";
            UndimIcons();
            IconDistance(false);

            if (_stats != null && _target == null)
            {
                try { _stats.Target = null; } catch { }
            }
        }

        /// <summary>
        /// Late every frame while flying: the game's icons and its character panel.
        ///
        /// The game shows the icon over a door, or over a person, while the player's own character
        /// is within a set distance of it. In the free camera that is the wrong test: the only
        /// icon wanted is the one on the door being aimed at. So every icon is made see-through
        /// except that one, and the game's distance is raised for as long as a door is aimed at,
        /// so that its icon comes on wherever the player's character was left.
        /// (The icon's own switches, selecting the door, placing the icon and its canvas groups
        /// do nothing without that distance, and the distance does nothing without them.)
        /// </summary>
        private void ShowHoverStats()
        {
            if (_icons == null || Time.unscaledTime - _iconsAt > 2f)
            {
                _icons = UnityEngine.Object.FindObjectsOfType<AC.Scene.Explore.UI.EventIcon>();
                _iconsAt = Time.unscaledTime;
            }

            AC.Scene.Explore.Gateway? aimed = _hoverDoor;
            IntPtr mine = aimed != null ? Safe(() => aimed.EventIcon.Pointer, IntPtr.Zero) : IntPtr.Zero;
            for (int i = 0; i < _icons.Length; i++)
            {
                AC.Scene.Explore.UI.EventIcon icon = _icons[i];
                if (icon == null) continue;
                try { IconAlpha(icon, icon.Pointer == mine ? 1f : 0f); } catch { }
            }
            _iconsDimmed = true;

            IconDistance(aimed != null);
            if (aimed != null) DoorIconHeld(aimed);

            if (_hoverActor != null)
            {
                ShowStats(_hoverActor);
                return;
            }
            if (_stats == null) return;
            try
            {
                if (_stats.Target != null) _stats.Target = null;
            }
            catch
            {
            }
        }

        /// <summary>
        /// The rest of what it takes for a far door's icon to show. Taking these out and leaving
        /// only the raised distance made the outdoor icons disappear again (FINDINGS section 33),
        /// so the distance is necessary but not enough. Which of these the game actually needs has
        /// not been separated: they are kept together because together they are known to work.
        /// </summary>
        private static void DoorIconHeld(AC.Scene.Explore.Gateway door)
        {
            try
            {
                AC.Scene.Explore.UI.EventIcon icon = door.EventIcon;
                if (icon == null) return;
                if (icon.VisibleAlways == false) icon.VisibleAlways = true;
                if (icon.VisibleDistance == false) icon.VisibleDistance = true;
                if (icon.Visible == false) icon.Visible = true;

                ExploreScene? scene = SceneSingleton<ExploreScene>.Instance;
                if (scene != null && scene._camera != null) icon.UpdatePosition(scene._camera);

                var groups = icon.GetComponentsInChildren<CanvasGroup>(true);
                for (int i = 0; i < groups.Length; i++)
                {
                    if (groups[i] != null && groups[i].alpha < 1f) groups[i].alpha = 1f;
                }
            }
            catch
            {
            }
        }

        // The game "selects" a door when the player stands at it, and un-selects it after.
        private static void DoorSelected(AC.Scene.Explore.Gateway door, bool selected)
        {
            try
            {
                AC.Scene.Explore.UI.EventIcon icon = door.EventIcon;
                if (icon != null)
                {
                    icon.VisibleAlways = selected;
                    icon.VisibleDistance = selected;
                    icon.Visible = selected;
                }
                if (selected) door.OnSelect();
                else door.AC_Scene_Explore_IEventHookable_OnDeselect();
            }
            catch
            {
            }
        }

        private void IconDistance(bool raised)
        {
            if (raised == _iconDistanceRaised) return;
            try
            {
                ExploreScene? scene = SceneSingleton<ExploreScene>.Instance;
                TrackingCamera? tracking = scene != null ? scene.TrackingCamera : null;
                AC.PropertyData.ExploreProperties? properties = tracking != null ? tracking._propertyData : null;
                if (properties == null) return;

                // _icon is a copy of the game's numbers: change the copy, hand it back whole.
                AC.PropertyData.ExploreProperties.IconProperty icons = properties._icon;
                if (raised)
                {
                    _iconDistanceBefore = icons.VisibilityThreshold;
                    icons.VisibilityThreshold = 100000f;
                }
                else
                {
                    icons.VisibilityThreshold = _iconDistanceBefore;
                }
                properties._icon = icons;
                _iconDistanceRaised = raised;
            }
            catch (Exception e)
            {
                Plugin.Debug("[spectate] icon distance: " + e.Message);
            }
        }

        // Draws or does not draw one icon, through its picture's transparency: the one thing about
        // an icon the game does not keep resetting.
        private static void IconAlpha(AC.Scene.Explore.UI.EventIcon icon, float alpha)
        {
            UnityEngine.UI.Image image = icon._image;
            if (image == null) return;
            Color colour = image.color;
            if (colour.a == alpha) return;
            colour.a = alpha;
            image.color = colour;
        }

        private void UndimIcons()
        {
            if (_iconsDimmed == false || _icons == null) return;
            _iconsDimmed = false;
            for (int i = 0; i < _icons.Length; i++)
            {
                AC.Scene.Explore.UI.EventIcon icon = _icons[i];
                if (icon == null) continue;
                try { IconAlpha(icon, 1f); } catch { }
            }
        }

        /// <summary>
        /// End while flying: the player's own character is put on the floor under the camera, and
        /// the camera goes back to them.
        /// </summary>
        private void Land(ExploreScene explore, TrackingCamera tracking)
        {
            Player player = explore.Player;
            Camera camera = explore._camera;
            if (player == null || camera == null) return;

            Vector3 floor = FlyPoint().position;
            if (Physics.Raycast(floor + Vector3.up * 0.3f, Vector3.down, out RaycastHit hit, 200f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                floor = hit.point;
            float facing = camera.transform.eulerAngles.y;

            // Back to the player first: that wakes them up where they were left.
            Stop(explore, tracking, "landed the free camera");

            GameObject spot = new GameObject("SpectatorSpot");
            try
            {
                spot.transform.position = floor;
                spot.transform.rotation = Quaternion.Euler(0f, facing, 0f);
                player.Warp(spot.transform);
                Plugin.Debug($"[spectate] landed the player at {floor}");
                Note("Your character is here now");
            }
            finally
            {
                Destroy(spot);
            }
        }

        // How far along the ray the point is, and how far off the ray it lies there.
        private static float Along(Ray ray, Vector3 point)
        {
            return Vector3.Dot(point - ray.origin, ray.direction);
        }

        private static float Off(Ray ray, Vector3 point, float along)
        {
            return Vector3.Distance(point, ray.origin + ray.direction * along);
        }

        /// <summary>
        /// The outdoor map is loaded in pieces. Each piece is marked in the world by colliders the
        /// game can look up; find the one the free camera is in or above, and ask for it.
        /// </summary>
        private void LoadAreaUnder(Vector3 point)
        {
            if (Time.unscaledTime - _lastAreaProbe < 0.5f) return;
            _lastAreaProbe = Time.unscaledTime;

            // Only outdoors. Inside a building the game has loaded the place itself, and the
            // outdoor pieces lie under it: asking for one there pulled outdoor scenery in.
            ExploreScene? scene = SceneSingleton<ExploreScene>.Instance;
            if (scene == null || Safe(() => scene.Player.IsOutdoor, true) == false)
            {
                _lastAreaFound = -2;
                return;
            }

            int found = -1;
            string how = "none";
            try
            {
                var around = Physics.OverlapSphere(point, 0.1f);
                for (int i = 0; i < around.Length && found < 0; i++)
                {
                    if (around[i] != null && AC.Scene.Explore.Area.TryGet(around[i], out AC.Scene.Explore.Area inside) && inside != null)
                    {
                        found = inside.ID;
                        how = "inside";
                    }
                }

                if (found < 0)
                {
                    var below = Physics.RaycastAll(point + Vector3.up * 0.5f, Vector3.down, 300f);
                    float nearest = float.MaxValue;
                    for (int i = 0; i < below.Length; i++)
                    {
                        RaycastHit under = below[i];
                        Collider collider = under.collider;
                        if (collider == null || under.distance >= nearest) continue;
                        if (AC.Scene.Explore.Area.TryGet(collider, out AC.Scene.Explore.Area area) == false || area == null) continue;

                        nearest = under.distance;
                        found = area.ID;
                        how = "below";
                    }
                }
            }
            catch (Exception e)
            {
                how = "failed: " + e.Message;
            }

            if (found != _lastAreaFound)
            {
                _lastAreaFound = found;
                _areaSince = Time.unscaledTime;
                Plugin.Debug($"[spectate] free camera is over area {found} ({how})");
            }

            // Along the seam between two pieces the answer flips back and forth, and every request
            // makes the game unload one piece and load another: scenery went missing. Ask only
            // once the camera has been over the same piece for a while.
            if (found >= 0 && Time.unscaledTime - _areaSince > 1.5f) LoadArea(found);
        }
    }
}
