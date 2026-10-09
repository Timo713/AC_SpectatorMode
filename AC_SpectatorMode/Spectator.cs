using System;
using System.Collections.Generic;
using System.Text;
using AC.Scene;
using AC.Scene.Explore;
using BepInEx.Configuration;
using UnityEngine;

namespace AC_SpectatorMode
{
    /// <summary>
    /// Runs every frame for the whole session and decides who the overworld camera follows.
    ///
    /// The game's TrackingCamera follows one Transform, its Follow field: for the player that is
    /// the "Look" object on the player's character. Every NPC has a Look object of its own, so
    /// watching someone else is pointing Follow at theirs, and going back is pointing it at the
    /// player's again. Orbiting, zoom, first person and wall collision stay exactly as they are,
    /// because none of them care whose Look it is. FINDINGS.md has the measurements.
    ///
    /// The camera does not follow the Look bone itself but a small object of this plugin's own,
    /// parented to it, so that the view can be raised or lowered without touching the character.
    /// It stays a child of the character, which is how AC_MainCameraExtension knows whose body to
    /// hide in first person.
    /// </summary>
    public partial class Spectator : MonoBehaviour
    {
        // Who is being watched. Null means nobody: the camera is the game's own, on the player.
        private Actor? _target;
        private Transform? _targetLook;
        private string _targetName = "";
        // Survives the character's object being rebuilt when a map loads.
        private int _targetId;

        // This plugin's own object under the Look bone; the camera follows this.
        private Transform? _anchor;
        // How far the drags have moved the view off the character, in metres, in world space.
        private Vector3 _shift;
        // Where the drags have asked it to be; _shift glides there.
        private Vector3 _shiftWanted;
        // To notice the game's own first-person key while watching: the camera's mode last
        // frame, and when the wheel or the right button (which also change it) were last used.
        private bool _modeKnown;
        private TrackingCamera.States _viewModeWas;
        private float _zoomInputAt = -10f;
        // To tell a tap of the right button from a drag, in the game's mouse mode.
        private float _rightAt;
        // When the camera last switched between watching and flying, so one click cannot do both.
        private float _lastSwitch = -100f;
        // Whether the right button went down while the camera was really in play: a click that
        // closes a menu or ends a pause is not a tap.
        private bool _rightValid;
        private float _leftMoved;
        private float _rightMoved;

        // True while this plugin, not the player, has switched the game's camera control off
        // (for the length of a right-button drag).
        private bool _weHold;
        // True from the moment the game's mouse mode is seen until the pointer is locked again.
        private bool _mouseMode;
        private bool _hidInMode;
        // Left-drag turning in the game's mouse mode: the angles last written, kept on the rig.
        private bool _turnHeld;
        private Vector2 _turnAngles;

        // The drag zoom glides: where it is heading.
        private float _distWanted;
        private bool _distEasing;

        // Set when the watched character left the map and following through doors is off.
        private bool _leftMap;

        // The game's own panel with a character's portrait, mood and relationship.
        private AC.Scene.Explore.UI.HeroineStatsUI? _stats;
        private float _statsLooked = -100f;

        // What the minimap was following before it was pointed at the watched character.
        private Transform? _mapHome;
        // This plugin's own object for the minimap to follow: the watched character's position,
        // but never their turning.
        private Transform? _mapProbe;

        // Where the player's own character was left when they were put away, or where the game
        // put them after the last door.
        private Vector3 _selfHome;

        // The minimap's marker for the player: what it followed before it was borrowed.
        private Transform? _blipHome;
        private Transform? _sightHome;
        private Transform? _sightTurnHome;
        // This plugin's own object for the marker: where the camera is over, facing the way it looks.
        private Transform? _blipProbe;

        // The game's "ears", and this plugin's own that ride on the camera while it is away.
        private AudioListener? _gameEars;
        private AudioListener? _ears;
        private bool _earsLogged;
        // True once the game's listener was found to be on the camera already: nothing to do.
        private bool _earsOnCamera;

        // Whether the game's mouse mode was on last frame, while watching.
        private bool _modeWas;

        private bool _mapBorrowed;

        // True while the player's own character is put away.
        private bool _selfHidden;
        // After the game took the camera for a scene: wake the player once the game is back to
        // normal, in case the game did not do it itself.
        private bool _wakeLater;
        private float _wakeSince = -1f;
        private bool _sleepAgain;

        private string _lastMode = "";

        private float _lastArea = -100f;

        // While a map is loading because the character went through a door, the usual reasons to
        // stop watching are ignored until this time.
        private float _travelUntil = -100f;
        private float _lastTravel = -100f;

        // How often the game pointed the camera somewhere else while watching.
        private int _putBack;

        private string _note = "";
        private float _noteUntil;

        private float _lastStatus = -100f;
        private float _lastRemark = -100f;
        private float _lastError = -100f;

        private void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - _lastError < 5f) return;
                _lastError = Time.unscaledTime;
                Plugin.Logger.LogWarning("Spectator update failed: " + e);
            }
        }

        private void Tick()
        {
            ExploreScene? explore = SceneSingleton<ExploreScene>.Instance;
            TrackingCamera? tracking = explore != null ? explore.TrackingCamera : null;
            if (explore != null) Hooks.InstallClothingMenu();
            if (explore == null || tracking == null)
            {
                // The overworld is gone, and the watched character with it.
                _weHold = false;
                Hooks.BlockInteraction = false;
                Forget();
                return;
            }

            if (Plugin.Enabled.Value == false)
            {
                if (_target != null || _flying) Stop(explore, tracking, "the plugin was switched off");
                ReleaseHold(tracking);
                Hooks.BlockInteraction = false;
                return;
            }

            if (_target != null)
            {
                if (Time.unscaledTime < _travelUntil)
                {
                    Reacquire(explore);
                }
                else
                {
                    string? reason = WhyStop(explore, tracking);
                    if (reason != null) Stop(explore, tracking, reason, reason.StartsWith("the character") == false);
                }
            }

            if (_flying && Time.unscaledTime >= _travelUntil)
            {
                string? reason = WhyStopFlying(explore, tracking);
                // An event or a talk needs the player and the camera: the game gets both back,
                // and that is the end of it. (Going back to the free camera afterwards was tried
                // and was worse: a visible delay, and once a black screen.)
                if (reason != null) Stop(explore, tracking, reason, true);
            }

            if (_wakeLater)
            {
                // The scene is over when the game's camera mode is the walking one again and the
                // overworld camera is drawing. Waiting for more than that (camera control back, no
                // text on screen) left the player unseen for a noticeable moment afterwards.
                if (WalkingCamera(explore, tracking))
                {
                    if (_wakeSince < 0f) _wakeSince = Time.unscaledTime;
                    {
                        _wakeLater = false;
                        Player back = explore.Player;
                        if (back != null)
                        {
                            try { back.WakeUp(); } catch { }
                            try { back.CharacterGrid.VisibleImmediate = true; } catch { }
                            try { back.CharacterGrid.Visible = true; } catch { }
                        }
                    }
                }
                else
                {
                    _wakeSince = -1f;
                }
            }

            if (CanSwitch(explore, tracking))
            {
                if (Pressed(Plugin.NextKey, Plugin.NextKey2)) Cycle(explore, tracking, 1);
                else if (Pressed(Plugin.PreviousKey, Plugin.PreviousKey2)) Cycle(explore, tracking, -1);
                else if ((_target != null || _flying) && Pressed(Plugin.BackKey, Plugin.BackKey2)) Stop(explore, tracking, "the back key");
                else if (Pressed(Plugin.FlyKey, Plugin.FlyKey2))
                {
                    if (_flying) Stop(explore, tracking, "the free camera key");
                    else StartFlying(explore, tracking);
                }
            }

            bool watching = _target != null && _targetLook != null;
            if (watching) Hold(explore, tracking);
            if (_flying) Fly(explore, tracking);

            if (watching && _leftMap)
            {
                // They went through a door and the doors setting is off: move on to someone who is
                // still here, or back to the player if there is nobody.
                _leftMap = false;
                if (Plugin.FlyWhenTheyLeave.Value)
                {
                    // Stay right here, as a free camera.
                    StartFlying(explore, tracking, true);
                }
                else
                {
                    IntPtr gone = _target!.Pointer;
                    Cycle(explore, tracking, 1);
                    if (_target != null && _target.Pointer == gone) Stop(explore, tracking, "they went somewhere else");
                }
                watching = _target != null && _targetLook != null;
            }

            bool free = CanSwitch(explore, tracking);

            // Measured (FINDINGS sections 13 and 14). The game's mouse mode (middle click) starts
            // as: camera control off AND the pointer out. Pressing the left button there makes the
            // game switch camera control back on while the pointer stays out, so the mode is
            // remembered from when it is first seen until the pointer is locked again.
            // In it the camera does not turn, so nothing needs holding, and the game's switch is
            // left alone. Outside it, a right-button drag borrows that switch to stop the camera
            // turning, and re-sets it every frame because the game keeps switching it back.
            bool walking = WalkingCamera(explore, tracking);
            bool pointerOut = Cursor.lockState != CursorLockMode.Locked;
            if (_weHold == false)
            {
                if (walking == false || pointerOut == false) _mouseMode = false;
                else if (tracking.ActiveControl == false) _mouseMode = true;
            }
            bool mouseMode = _mouseMode && _weHold == false;
            bool rightDrag = (watching || _flying) && Input.GetMouseButton(1) && (mouseMode || free || _weHold);

            if (rightDrag && mouseMode == false)
            {
                _weHold = true;
                if (tracking.ActiveControl) tracking.ActiveControl = false;
                HidePointer();
            }
            else
            {
                ReleaseHold(tracking);
            }

            // In mouse mode the pointer only goes out of sight for the length of a drag. A left
            // click that has not moved yet is a click on one of the game's buttons, not a drag.
            if ((watching || _flying) && mouseMode && Input.GetMouseButton(0))
                _leftMoved += Mathf.Abs(Input.GetAxis("Mouse X")) + Mathf.Abs(Input.GetAxis("Mouse Y"));
            else
                _leftMoved = 0f;

            if (mouseMode && (rightDrag || _leftMoved > 3f))
            {
                Cursor.visible = false;
                _hidInMode = true;
            }
            else if (_hidInMode)
            {
                _hidInMode = false;
                if (Cursor.lockState != CursorLockMode.Locked) Cursor.visible = true;
            }

            // While watching, holding the right button and dragging is this plugin's pan and zoom;
            // a quick tap without dragging lets go of the character and flies from here. (In the
            // free camera a right click on a character goes back to watching: FreeCamera.cs.)
            if ((watching || _flying) && Input.GetMouseButtonDown(1))
            {
                _rightAt = Time.unscaledTime;
                _rightMoved = 0f;
                _rightValid = (free || mouseMode) && Time.timeScale > 0f;
            }
            if ((watching || _flying) && Input.GetMouseButton(1))
                _rightMoved += Mathf.Abs(Input.GetAxis("Mouse X")) + Mathf.Abs(Input.GetAxis("Mouse Y"));
            // The same rule as the free camera in conversations (AC_ADVFreeCamera, "tap only"): a
            // press counts as a tap only if it was short and the mouse stayed put. The action
            // happens on release, and not twice in quick succession.
            if ((watching || _flying) && (free || mouseMode) && Input.GetMouseButtonUp(1)
                && Time.unscaledTime - _rightAt <= Plugin.TapLength.Value && _rightMoved < 3f
                && Time.unscaledTime - _lastSwitch > 0.35f && _rightValid)
            {
                _lastSwitch = Time.unscaledTime;
                ReleaseHold(tracking);
                if (_flying)
                {
                    // Whatever the camera is aimed at: a character is watched, a door gone through.
                    Aim(explore, tracking);
                }
                else
                {
                    Plugin.Debug("[spectate] right tap while watching: free camera");
                    StartFlying(explore, tracking);
                }
                watching = _target != null && _targetLook != null;
                rightDrag = false;
            }

            _turnHeld = false;
            if (rightDrag)
            {
                if (_flying) FlyDrag(explore, tracking);
                // In first person the left button is not part of the drag, as on the player's
                // own camera.
                else Drag(explore, tracking, Input.GetMouseButton(0) && tracking.State != TrackingCamera.States.FPS);
            }
            else if ((watching || _flying) && mouseMode && Input.GetMouseButton(0))
            {
                Turn(tracking);
            }

            if (watching) EaseZoom(tracking);

            // The game's own first-person key (Space) also puts the view back, in either
            // direction. The wheel and the right-button drag change the cameraMode too
            // (AC_MainCameraExtension), and those must not reset anything.
            if (Input.mouseScrollDelta.y != 0f || Input.GetMouseButton(1)) _zoomInputAt = Time.unscaledTime;
            TrackingCamera.States cameraMode = tracking.State;
            bool viewMode = cameraMode == TrackingCamera.States.TPS || cameraMode == TrackingCamera.States.FPS;
            if (watching && _modeKnown && viewMode && cameraMode != _viewModeWas && Time.unscaledTime - _zoomInputAt > 0.15f)
            {
                _shiftWanted = Vector3.zero;
                _distEasing = false;
                AC.PropertyData.ExploreProperties properties = tracking._propertyData;
                float distance = properties != null ? properties._camera.DefaultDistance : 0f;
                if (distance > 0.01f)
                {
                    TrackingCamera.AngleParameter orbit = tracking._parameter;
                    orbit.Distance = distance;
                    tracking._parameter = orbit;
                }
                Plugin.Debug("[spectate] the game's first-person key: view reset");
            }
            _modeKnown = watching && viewMode;
            _viewModeWas = cameraMode;

            if (watching && (free || mouseMode) && Pressed(Plugin.ResetKey, Plugin.ResetKey2))
            {
                _shiftWanted = Vector3.zero;
                Note("View centred");
            }

            if (watching && free && Pressed(Plugin.BringKey, Plugin.BringKey2) && Bring(explore)
                && Plugin.BringReturns.Value)
            {
                Stop(explore, tracking, "brought over");
                watching = false;
            }
            else if (_flying && free && Pressed(Plugin.BringKey, Plugin.BringKey2))
            {
                Land(explore, tracking);
            }

            // While watching, the player's own character ignores the buttons that talk to people
            // and use doors. With the game's pointer out, left click is the game's again.
            // Worked out afresh from what is true now, not from earlier in this frame: for the one
            // frame of a switch between flying and watching the old answer was "neither", and the
            // game saw the right click as its interact button (it walked the player out of a door).
            // Also on while a map is loading, when the player's character is awake at a door.
            bool away = (_target != null && _targetLook != null) || _flying;
            Hooks.BlockInteraction = away && (free || mouseMode || Time.unscaledTime < _travelUntil + 1f);
            Hooks.AllowClick = mouseMode;

            // Clothing State Menu comes and goes with the game's pointer, while watching.
            bool modeNow = mouseMode && _target != null && _targetLook != null;
            if (modeNow != _modeWas)
            {
                _modeWas = modeNow;
                if (Plugin.ClothingWithMouse.Value) Hooks.ShowClothingMenu(modeNow);
            }

            if (Plugin.DebugInfo.Value)
            {
                string mode = $"control {tracking.ActiveControl} (ours {_weHold})  pointer {Cursor.lockState}/{(Cursor.visible ? "shown" : "hidden")}" +
                              $"  icons {Safe(() => explore.IsActiveIcon, false)}  overlap {(global::Manager.Scene.IsOverlap)}  mode {tracking.State}";
                if (mode != _lastMode)
                {
                    _lastMode = mode;
                    Plugin.Logger.LogInfo("[spectate] game state: " + mode);
                }
            }
        }

        // Switching camera control off also brings the pointer out; outside the game's mouse mode
        // it should not show.
        private static void HidePointer()
        {
            if (Cursor.lockState != CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Locked;
            if (Cursor.visible) Cursor.visible = false;
        }

        private void PutSelfAway(Player player, bool always = false)
        {
            if (_selfHidden || player == null) return;
            if (always == false && Plugin.HideSelf.Value == false) return;
            _selfHidden = true;
            _wakeLater = false;
            Hooks.HideSelf = true;
            try { player.Sleep(); } catch (Exception e) { Plugin.Debug("[spectate] Player.Sleep failed: " + e.Message); }
            try { player.CharacterGrid.Visible = false; } catch { }
            _selfHome = player.Transform.position;
            Plugin.Debug("[spectate] the player's character is put away");
        }

        /// <summary>
        /// Late every frame while the player's character is put away. Waking them for a map
        /// change made them show for the seconds of the fade; nothing is allowed to show them.
        /// </summary>
        private void KeepSelfUnseen()
        {
            // Also through a scene the game started while the camera was away (_wakeLater): walking
            // in normally the player is not in the picture, and the measured difference is that
            // here they stand awake in their idle state at the door, in plain view.
            if (_selfHidden == false && _wakeLater == false) return;
            try
            {
                ExploreScene? explore = SceneSingleton<ExploreScene>.Instance;
                Player? player = explore != null ? explore.Player : null;
                var grid = player != null ? player.CharacterGrid : null;
                if (grid == null) return;
                if (grid.Visible) grid.VisibleImmediate = false;
                var human = grid.Chara;
                if (human != null && human.visibleAll) human.visibleAll = false;
            }
            catch
            {
            }
        }

        private void BringSelfBack(Player player, bool show = true)
        {
            if (_selfHidden == false) return;
            _selfHidden = false;
            _sleepAgain = false;
            Hooks.HideSelf = false;
            if (player == null) return;
            // In a scene the game puts its characters to sleep and wakes them itself; waking the
            // player there made them appear in the middle of it.
            if (show == false)
            {
                _wakeLater = true;
                _wakeSince = -1f;
                Plugin.Debug("[spectate] the game has the player's character for a scene");
                return;
            }
            try { player.WakeUp(); } catch (Exception e) { Plugin.Debug("[spectate] Player.WakeUp failed: " + e.Message); }
            // In an event the game hides and shows its characters itself; forcing the player visible
            // there put them in the middle of the scene. The game's own per-frame check brings them
            // back when the scene is over.
            if (show)
            {
                try { player.CharacterGrid.Visible = true; } catch { }
            }
            Plugin.Debug("[spectate] the player's character is back");
        }

        /// <summary>The walking camera, whether or not something is under the pointer.</summary>
        private static bool WalkingCamera(ExploreScene explore, TrackingCamera tracking)
        {
            TrackingCamera.States state = tracking.State;
            if (state != TrackingCamera.States.TPS && state != TrackingCamera.States.FPS) return false;
            Camera camera = explore._camera;
            return camera != null && camera.enabled;
        }

        // The game undoes the hold between this plugin's turns; do it again late in the frame and
        // while the screen is drawn, so neither the camera nor the pointer gets a moment to show.
        private void HoldAgain()
        {
            if (_hidInMode) Cursor.visible = false;
            if (_weHold == false) return;
            ExploreScene? explore = SceneSingleton<ExploreScene>.Instance;
            TrackingCamera? tracking = explore != null ? explore.TrackingCamera : null;
            if (tracking != null && tracking.ActiveControl) tracking.ActiveControl = false;
            HidePointer();
        }

        private void ReleaseHold(TrackingCamera? tracking)
        {
            if (_weHold == false) return;
            _weHold = false;
            if (tracking != null) tracking.ActiveControl = true;
        }

        /// <summary>
        /// With the game's pointer out: dragging with the left button turns the camera.
        ///
        /// Measured: in that mode the game does not act on the orbit's angles (writing them alone
        /// changed nothing), so the rig's follow point, which is what Cinemachine actually orbits,
        /// is turned as well. The angles are still written so the game carries on from them.
        /// </summary>
        private void Turn(TrackingCamera tracking)
        {
            TrackingCamera.AngleParameter orbit = tracking._parameter;
            float speed = Plugin.DragTurnSpeed.Value;
            Vector2 angles = orbit.EulerAngles;
            angles.y += Input.GetAxis("Mouse X") * speed;
            angles.x = Mathf.Clamp(angles.x - Input.GetAxis("Mouse Y") * speed, tracking._verticalMin, tracking._verticalMax);
            orbit.EulerAngles = angles;
            tracking._parameter = orbit;

            _turnAngles = angles;
            _turnHeld = true;
            TurnAgain();
        }

        private void TurnAgain()
        {
            if (_turnHeld == false) return;
            TrackingCamera? tracking = SceneSingleton<ExploreScene>.Instance != null
                ? SceneSingleton<ExploreScene>.Instance.TrackingCamera : null;
            if (tracking == null) return;

            // Both follow points: the circling camera uses one, first person (and so the free
            // camera) the other.
            Quaternion facing = Quaternion.Euler(_turnAngles.x, _turnAngles.y, 0f);
            Transform rig = tracking._followBodyTPS;
            if (rig != null) rig.rotation = facing;
            Transform head = tracking._followBodyFPS;
            if (head != null) head.rotation = facing;
        }

        /// <summary>The drag zoom glides to where the drag left it, instead of jumping.</summary>
        private void EaseZoom(TrackingCamera tracking)
        {
            if (_distEasing == false) return;
            // The wheel takes over at once.
            if (Input.mouseScrollDelta.y != 0f)
            {
                _distEasing = false;
                return;
            }

            TrackingCamera.AngleParameter orbit = tracking._parameter;
            float smoothing = Plugin.DragSmoothing.Value;
            float step = smoothing <= 0.001f ? 1f : 1f - Mathf.Exp(-Mathf.Min(Time.unscaledDeltaTime, 0.1f) / smoothing);
            float next = Mathf.Lerp(orbit.Distance, _distWanted, step);
            if (Mathf.Abs(next - _distWanted) < 0.002f)
            {
                next = _distWanted;
                _distEasing = false;
            }
            orbit.Distance = next;
            tracking._parameter = orbit;
        }

        /// <summary>Moves the player's own character next to the watched one, on the same map.</summary>
        private bool Bring(ExploreScene explore)
        {
            Player player = explore.Player;
            Actor watched = _target!;
            Transform there = watched.Transform;
            if (player == null || there == null) return false;
            if (Safe(() => watched.MapID, -1) != Safe(() => player.MapID, -2))
            {
                Note("They are somewhere else right now");
                return false;
            }

            // The game's own warp takes a place to stand: one step in front of them, facing them.
            GameObject spot = new GameObject("SpectatorSpot");
            try
            {
                spot.transform.position = there.position + there.forward;
                spot.transform.rotation = Quaternion.LookRotation(-there.forward, Vector3.up);
                BringSelfBack(player);
                player.Warp(spot.transform);
                Plugin.Debug($"[spectate] brought the player to {spot.transform.position}");
                Note("Your character is with " + _targetName);
            }
            finally
            {
                Destroy(spot);
            }
            return true;
        }

        private void LateUpdate()
        {
            HoldAgain();
            TurnAgain();
            KeepSelfUnseen();

            if (_target != null && _targetLook != null) ShowStats(_target);
            else if (_flying) ShowHoverStats();

            // The minimap has one thing it follows: the watched character, or the free camera.
            Vector3 focus;
            if (_target != null && _targetLook != null)
            {
                Transform body = _target.Transform;
                if (body == null) return;
                focus = body.position;
            }
            else if (_flying && _flyPoint != null)
            {
                // At floor level: the height of the floor the player's character was left on.
                focus = new Vector3(_flyPoint.position.x, _selfHome.y, _flyPoint.position.z);
            }
            else
            {
                MapHome();
                return;
            }

            ExploreScene? explore = SceneSingleton<ExploreScene>.Instance;
            var map = explore != null ? explore._minimap : null;
            if (map == null) return;

            if (_mapBorrowed == false)
            {
                _mapHome = map._cameraTrackingTarget;
                _mapBorrowed = true;
            }
            // Following the character's own object made the minimap twist as they turned.
            if (_mapProbe == null)
            {
                GameObject made = new GameObject("SpectatorMapTarget");
                DontDestroyOnLoad(made);
                _mapProbe = made.transform;
            }
            // Measured: the game's own target ("t_minimap") rides 100 m above the player, and the
            // minimap's camera sits exactly on it looking down. At the character's feet the camera
            // was inside the ground, which is what made the picture go wrong.
            float lift = _mapHome != null ? _mapHome.localPosition.y : 0f;
            if (lift < 10f) lift = 100f;
            _mapProbe.position = focus + Vector3.up * lift;
            if (_mapHome != null) _mapProbe.rotation = _mapHome.rotation;

            Transform now = map._cameraTrackingTarget;
            if (now == null || now.Pointer != _mapProbe.Pointer) map._cameraTrackingTarget = _mapProbe;

            // The player's marker. While watching someone the minimap is about them, and the
            // marker and its cone are simply not drawn. In the free camera the marker stands for
            // "where I am looking from": in the middle, pointing the way the camera looks.
            try
            {
                bool watchingNow = _target != null && _targetLook != null;
                var blip = map._player;
                var sight = map._playerSight;
                MarkerShown(blip, sight, watchingNow == false);

                if (watchingNow)
                {
                    MarkerHome(map);
                }
                else
                {
                    if (_blipProbe == null)
                    {
                        GameObject made = new GameObject("SpectatorMapMarker");
                        DontDestroyOnLoad(made);
                        _blipProbe = made.transform;
                    }
                    Camera view = explore!._camera;
                    float facing = view != null ? view.transform.eulerAngles.y : 0f;
                    _blipProbe.SetPositionAndRotation(focus, Quaternion.Euler(0f, facing, 0f));

                    if (blip != null)
                    {
                        if (_blipHome == null) _blipHome = blip.Target;
                        if (blip.Target == null || blip.Target.Pointer != _blipProbe.Pointer) blip.Target = _blipProbe;
                    }
                    if (sight != null)
                    {
                        if (_sightHome == null) _sightHome = sight.Target;
                        if (sight.Target == null || sight.Target.Pointer != _blipProbe.Pointer) sight.Target = _blipProbe;
                        if (_sightTurnHome == null) _sightTurnHome = sight.RotateTarget;
                        if (sight.RotateTarget == null || sight.RotateTarget.Pointer != _blipProbe.Pointer) sight.RotateTarget = _blipProbe;
                    }
                }
            }
            catch
            {
                // The marker is a nicety.
            }

            Ears(explore!);
        }

        /// <summary>
        /// Points the game's own character panel at the watched character, instead of whoever
        /// stands next to the player's character.
        /// </summary>
        private void ShowStats(Actor actor)
        {
            if (_stats == null)
            {
                if (Time.unscaledTime - _statsLooked < 2f) return;
                _statsLooked = Time.unscaledTime;
                _stats = UnityEngine.Object.FindObjectOfType<AC.Scene.Explore.UI.HeroineStatsUI>(true);
                if (_stats == null) return;
                Plugin.Debug("[spectate] found the game's character panel");
            }

            try
            {
                Actor shown = _stats.Target;
                if (shown == null || shown.Pointer != actor.Pointer) _stats.Target = actor;
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - _lastRemark < 5f) return;
                _lastRemark = Time.unscaledTime;
                Plugin.Debug("[spectate] the character panel refused: " + e.Message);
            }
        }

        private void MapHome()
        {
            if (_mapBorrowed == false) return;
            _mapBorrowed = false;

            ExploreScene? explore = SceneSingleton<ExploreScene>.Instance;
            var map = explore != null ? explore._minimap : null;
            if (map != null && _mapHome != null) map._cameraTrackingTarget = _mapHome;
            _mapHome = null;

            try
            {
                if (map != null)
                {
                    MarkerHome(map);
                    MarkerShown(map._player, map._playerSight, true);
                }
            }
            catch
            {
            }
            _blipHome = null;
            _sightHome = null;
            _sightTurnHome = null;

            EarsHome();
        }

        // Gives the marker and its cone back what they followed before the free camera took them.
        private void MarkerHome(AC.Scene.Explore.UI.Map.Minimap map)
        {
            if (_blipHome != null && map._player != null) map._player.Target = _blipHome;
            if (_sightHome != null && map._playerSight != null) map._playerSight.Target = _sightHome;
            if (_sightTurnHome != null && map._playerSight != null) map._playerSight.RotateTarget = _sightTurnHome;
            _blipHome = null;
            _sightHome = null;
            _sightTurnHome = null;
        }

        // Draws or does not draw the marker and its cone, by making their pictures see-through.
        // Nothing is switched off, so the minimap's own layout is left alone.
        private static void MarkerShown(AC.Scene.Explore.UI.Map.Icon blip, AC.Scene.Explore.UI.Map.Icon sight, bool shown)
        {
            float alpha = shown ? 1f : 0f;
            foreach (AC.Scene.Explore.UI.Map.Icon icon in new[] { blip, sight })
            {
                if (icon == null) continue;
                UnityEngine.UI.Image image = icon._image;
                if (image == null) continue;
                Color colour = image.color;
                if (colour.a == alpha) continue;
                colour.a = alpha;
                image.color = colour;
            }
        }

        /// <summary>
        /// Sound is heard from wherever the game's listener is. If that is not on the camera, it
        /// stays behind with the player's character and the watched scene goes silent, so a
        /// listener of this plugin's own rides on the camera while it is away.
        /// </summary>
        private void Ears(ExploreScene explore)
        {
            Camera camera = explore._camera;
            if (camera == null) return;

            if (_earsOnCamera) return;
            if (_gameEars == null && _ears == null)
            {
                AudioListener found = UnityEngine.Object.FindObjectOfType<AudioListener>();
                if (found == null) return;
                bool onCamera = found.transform.IsChildOf(camera.transform);
                if (_earsLogged == false)
                {
                    _earsLogged = true;
                    Plugin.Debug($"[spectate] the game listens from {Path(found.transform)} (on the camera: {onCamera})");
                }
                if (onCamera)
                {
                    _earsOnCamera = true;
                    return;
                }

                _gameEars = found;
                found.enabled = false;

                GameObject made = new GameObject("SpectatorEars");
                DontDestroyOnLoad(made);
                _ears = made.AddComponent<AudioListener>();
            }

            if (_ears != null) _ears.transform.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
        }

        private void EarsHome()
        {
            if (_ears != null) Destroy(_ears.gameObject);
            _ears = null;
            if (_gameEars != null) _gameEars.enabled = true;
            _gameEars = null;
        }

        // ---------------------------------------------------------------------------------------
        // When
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Watching ends by itself when the character disappears or the game needs its camera: an
        /// event or a conversation is about the player. Null means carry on.
        /// </summary>
        private string? WhyStop(ExploreScene explore, TrackingCamera tracking)
        {
            // A Unity object that has been destroyed compares equal to null.
            if (_targetLook == null) return "the character is no longer there";

            TrackingCamera.States state = tracking.State;
            if (state != TrackingCamera.States.TPS && state != TrackingCamera.States.FPS)
                return "the game took the camera (mode " + state + ")";

            // Conversations switch the overworld camera's component off.
            Camera camera = explore._camera;
            if (camera == null || camera.enabled == false) return "a conversation started";

            return null;
        }

        /// <summary>
        /// The keys and the mouse only act on the walking camera, not in conversations or over text.
        /// The game's own mouse mode (middle click) counts as the walking camera.
        /// </summary>
        private static bool CanSwitch(ExploreScene explore, TrackingCamera tracking)
        {
            TrackingCamera.States state = tracking.State;
            if (state != TrackingCamera.States.TPS && state != TrackingCamera.States.FPS) return false;

            Camera camera = explore._camera;
            if (camera == null || camera.enabled == false) return false;

            return global::Manager.Scene.IsOverlap == false;
        }

        private static bool Pressed(ConfigEntry<KeyCode> first, ConfigEntry<KeyCode> second)
        {
            return (first.Value != KeyCode.None && Input.GetKeyDown(first.Value))
                || (second.Value != KeyCode.None && Input.GetKeyDown(second.Value));
        }

        // ---------------------------------------------------------------------------------------
        // Who
        // ---------------------------------------------------------------------------------------

        private void Cycle(ExploreScene explore, TrackingCamera tracking, int direction)
        {
            // With Debug Info on, the first press from the player logs every actor and why it is
            // or is not on the list.
            StringBuilder? table = Plugin.DebugInfo.Value && _target == null ? new StringBuilder() : null;
            List<Actor> candidates = Candidates(explore, table);
            if (table != null) Plugin.Logger.LogInfo("[spectate] actors:\n" + table.ToString().TrimEnd());

            if (candidates.Count == 0)
            {
                if (_target == null && _flying == false && Plugin.FlyWhenNobody.Value) StartFlying(explore, tracking);
                else Note("Nobody to watch here");
                return;
            }

            int index = -1;
            if (_target != null)
            {
                IntPtr current = _target.Pointer;
                index = candidates.FindIndex(actor => actor.Pointer == current);
            }

            int next;
            if (index >= 0)
            {
                next = (index + direction + candidates.Count) % candidates.Count;
            }
            else
            {
                // Starting from the player: begin with whoever is nearest.
                Vector3 from = _targetLook != null ? _targetLook.position
                    : _flying && _flyPoint != null ? _flyPoint.position
                    : explore.Player.Transform.position;
                next = 0;
                float best = float.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    float distance = (candidates[i].Transform.position - from).sqrMagnitude;
                    if (distance >= best) continue;
                    best = distance;
                    next = i;
                }
            }

            Watch(tracking, candidates[next]);
        }

        /// <summary>
        /// Everyone who can be watched right now, in the game's own order so that next and
        /// previous are stable: characters on the player's map whose object is switched on.
        /// </summary>
        private static List<Actor> Candidates(ExploreScene explore, StringBuilder? table)
        {
            List<Actor> sameMap = new List<Actor>();

            Player player = explore.Player;
            var actors = explore._actors;
            if (player == null || actors == null) return sameMap;

            IntPtr playerPointer = player.Pointer;
            int playerMap = Safe(() => player.MapID, -1);
            Vector3 playerPosition = player.Transform.position;
            table?.AppendLine($"  player: map {playerMap}  area {Safe(() => player.AreaID, -1)}  outdoor {Safe(() => player.IsOutdoor, false)}  at {playerPosition}");

            for (int i = 0; i < actors.Count; i++)
            {
                Actor actor = actors[i];
                if (actor == null || actor.Pointer == playerPointer) continue;

                Transform? root = Safe<Transform?>(() => actor.Transform, null);
                Transform? look = Safe(() => LookOf(actor), null);
                bool active = root != null && root.gameObject.activeInHierarchy;
                int map = Safe(() => actor.MapID, -1);
                bool usable = look != null && active;

                if (usable && map == playerMap) sameMap.Add(actor);

                table?.AppendLine(
                    $"  #{i} {Safe(() => actor.GetIl2CppType().Name, "?")} \"{Safe(() => actor.ObjectName, "?")}\" {NameOf(actor)}" +
                    $"  map {map}  area {Safe(() => actor.AreaID, -1)}  outdoor {Safe(() => actor.IsOutdoor, false)}" +
                    $"  shown {Safe(() => actor.CharacterGrid.Visible, false)}  active {active}  look {(look != null ? "yes" : "NO")}" +
                    $"  {(root != null ? Vector3.Distance(playerPosition, root.position).ToString("F1") : "?")} m from the player" +
                    (usable && map == playerMap ? "  [listed]" : ""));
            }

            // Only people on the map that is loaded. Someone on another map would need that map
            // loaded and the player's character dragged there, which is not what a key press
            // asking "who is here?" should do.
            return sameMap;
        }

        /// <summary>The point on a character that the game's camera follows.</summary>
        private static Transform? LookOf(Actor actor)
        {
            Transform? look = null;

            NPC? npc = actor.TryCast<NPC>();
            if (npc != null) look = npc._lookAt;

            if (look == null)
            {
                UniqueNPC? unique = actor.TryCast<UniqueNPC>();
                if (unique != null) look = unique._lookAt;
            }

            if (look == null)
            {
                Player? player = actor.TryCast<Player>();
                if (player != null) look = player._lookAt;
            }

            if (look == null)
            {
                Transform root = actor.Transform;
                if (root != null) look = root.Find("Look");
            }

            return look != null ? look : null;
        }

        private static string NameOf(Actor actor)
        {
            string name = Safe(() => actor.CharacterGrid.Chara.fileParam.fullname, "");
            if (string.IsNullOrWhiteSpace(name)) name = Safe(() => actor.ObjectName, "someone");
            return name;
        }

        private static T Safe<T>(Func<T> read, T fallback)
        {
            try
            {
                T value = read();
                return value == null ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }

        // ---------------------------------------------------------------------------------------
        // Watching
        // ---------------------------------------------------------------------------------------

        private void Watch(TrackingCamera tracking, Actor actor)
        {
            Transform? look = LookOf(actor);
            if (look == null) return;

            // Coming from the free camera: it ends here, and the camera distance it took comes back.
            EndFlying(tracking, true);
            // A right click that picked this character must not also count as a tap on them.
            _rightAt = -100f;

            DropAnchor();
            _target = actor;
            _targetLook = look;
            _targetName = NameOf(actor);
            _targetId = Safe(() => actor.UniqueID, 0);
            _putBack = 0;
            _shift = Vector3.zero;
            _shiftWanted = Vector3.zero;
            _distEasing = false;
            _lastStatus = Time.unscaledTime;

            tracking.Follow = Anchor(look);

            ExploreScene? scene = SceneSingleton<ExploreScene>.Instance;
            if (scene != null) PutSelfAway(scene.Player);

            Hooks.Watched = Safe<global::Character.Human?>(() => actor.CharacterGrid.Chara, null);
            Hooks.RefreshClothingMenu();

            Note("Watching " + _targetName);
            Plugin.Debug($"[spectate] watching {_targetName} ({Safe(() => actor.ObjectName, "?")}, id {_targetId}): camera now follows {Path(look)}");
        }

        /// <summary>This plugin's object under the Look bone, made on first use.</summary>
        private Transform Anchor(Transform look)
        {
            if (_anchor != null) return _anchor;

            GameObject made = new GameObject("SpectatorAnchor");
            Transform anchor = made.transform;
            anchor.SetParent(look, false);
            _anchor = anchor;
            return anchor;
        }

        private void DropAnchor()
        {
            if (_anchor != null) Destroy(_anchor.gameObject);
            _anchor = null;
        }

        /// <summary>
        /// A map load rebuilds or moves the characters. Find the watched one again by its id.
        /// </summary>
        private void Reacquire(ExploreScene explore)
        {
            if (_targetLook != null) return;

            var actors = explore._actors;
            if (actors == null) return;
            for (int i = 0; i < actors.Count; i++)
            {
                Actor actor = actors[i];
                if (actor == null || Safe(() => actor.UniqueID, 0) != _targetId) continue;

                Transform? look = Safe(() => LookOf(actor), null);
                if (look == null) return;

                _target = actor;
                _targetLook = look;
                _anchor = null;
                Hooks.Watched = Safe<global::Character.Human?>(() => actor.CharacterGrid.Chara, null);
                Hooks.RefreshClothingMenu();
                Plugin.Debug($"[spectate] found {_targetName} again after the map load");
                return;
            }
        }

        /// <summary>
        /// The watched character is on another map: ask the game to go there. This is the game's
        /// own map change, the one a door uses.
        /// </summary>
        private void Travel(ExploreScene explore, int map, int from)
        {
            _lastTravel = Time.unscaledTime;
            _travelUntil = Time.unscaledTime + 8f;
            // The map change puts the player's character at the door; put them away again after.
            _sleepAgain = _selfHidden;
            Player player = explore.Player;
            Plugin.Debug($"[spectate] {_targetName} is on map {map}, the game is showing map {from}: asking the game to change map." +
                         $" Player at {(player != null ? player.Transform.position.ToString() : "?")}");
            // Awake, the game puts the player's character at the door of the new map; asleep it
            // left them at meaningless coordinates. They are put away again once the map is in.
            if (_selfHidden && player != null)
            {
                try { player.WakeUp(); } catch { }
            }

            try
            {
                // The second argument is an optional spot number; an empty one leaves the choice to the game.
                explore.ChangeMap(map, new Il2CppSystem.Nullable<int>());
            }
            catch (Exception e)
            {
                _travelUntil = -100f;
                Plugin.Logger.LogWarning("Changing map failed: " + e.Message);
            }
        }

        /// <summary>
        /// The right-button drag: up and down raises the view, left and right zooms.
        /// With the left button held as well, as in the character maker: up and down moves the
        /// camera nearer or further, left and right slides the view sideways.
        /// </summary>
        private void Drag(ExploreScene explore, TrackingCamera tracking, bool both)
        {
            float across = Input.GetAxis("Mouse X");
            float up = Input.GetAxis("Mouse Y");
            if (across == 0f && up == 0f) return;

            float pan = Plugin.DragPanSpeed.Value;
            float zoom = Plugin.DragZoomSpeed.Value;
            float closer;

            if (both)
            {
                // Sideways as the camera sees it, kept level.
                Vector3 right = explore._camera.transform.right;
                right.y = 0f;
                Vector3 flat = new Vector3(_shiftWanted.x, 0f, _shiftWanted.z) + right.normalized * (across * pan);
                flat = Vector3.ClampMagnitude(flat, 2f);
                _shiftWanted.x = flat.x;
                _shiftWanted.z = flat.z;
                closer = up * pan;
            }
            else
            {
                _shiftWanted.y = Mathf.Clamp(_shiftWanted.y + up * pan, -1.5f, 1.5f);
                closer = -across * zoom;
            }

            if (closer == 0f) return;
            // In first person there is no distance to change (AC_MainCameraExtension works the
            // lens there); remembering one would make the camera jump on the way back out.
            if (tracking.State == TrackingCamera.States.FPS) return;
            // Only the target moves here; EaseZoom glides the camera to it.
            if (_distEasing == false) _distWanted = tracking._parameter.Distance;
            _distWanted = Mathf.Clamp(_distWanted - closer, 0f, 30f);
            _distEasing = true;
        }

        /// <summary>
        /// The outdoor map is loaded in pieces ("areas") around the player's character. While
        /// watching, ask the game for the piece the watched character is in instead.
        /// </summary>
        private void LoadArea(int area)
        {
            if (area < 0 || Time.unscaledTime - _lastArea < 1f) return;
            AC.AreaManager manager = AC.AreaManager._instance;
            if (manager == null || manager._areaID == area || manager._isLoading || manager._isUnloading) return;

            _lastArea = Time.unscaledTime;
            Plugin.Debug($"[spectate] the game has area {manager._areaID} loaded, asking for area {area}");
            manager.Set(area);
        }

        /// <summary>Every frame while watching: keep the camera on the character, and report.</summary>
        private void Hold(ExploreScene explore, TrackingCamera tracking)
        {
            Transform look = _targetLook!;
            Transform anchor = Anchor(look);

            // While the walking camera is in charge, the player's own character ignores the
            // buttons that talk to people and use doors. In menus and over text they work again.
            bool free = CanSwitch(explore, tracking);

            // In world space, whatever way the neck is leaning.
            if (_shift != _shiftWanted)
            {
                float smoothing = Plugin.DragSmoothing.Value;
                float glide = smoothing <= 0.001f ? 1f : 1f - Mathf.Exp(-Mathf.Min(Time.unscaledDeltaTime, 0.1f) / smoothing);
                _shift = Vector3.Lerp(_shift, _shiftWanted, glide);
                if ((_shift - _shiftWanted).sqrMagnitude < 0.000004f) _shift = _shiftWanted;
            }
            if (_shift != Vector3.zero) anchor.position = look.position + _shift;
            else anchor.localPosition = Vector3.zero;

            Transform now = tracking.Follow;
            if (now == null || now.Pointer != anchor.Pointer)
            {
                _putBack++;
                if (Time.unscaledTime - _lastRemark > 1f)
                {
                    _lastRemark = Time.unscaledTime;
                    Plugin.Debug($"[spectate] the game pointed the camera at {(now != null ? Path(now) : "nothing")}" +
                                 $" ({_putBack} times so far); pointing it back at {_targetName}");
                }
                tracking.Follow = anchor;
            }

            if (free && Plugin.FollowIntoBuildings.Value == false && Time.unscaledTime > _travelUntil)
            {
                Player stayed = explore.Player;
                Actor leaver = _target!;
                int theirs = Safe(() => leaver.MapID, -1);
                int ours = stayed != null ? Safe(() => stayed.MapID, -1) : -1;
                if (theirs >= 0 && ours >= 0 && theirs != ours) _leftMap = true;
                else if (theirs >= 0 && theirs == ours) RememberEye(explore._camera.transform.position);
            }

            if (free && Plugin.FollowIntoBuildings.Value && Time.unscaledTime > _travelUntil
                && Time.unscaledTime - _lastTravel > 3f)
            {
                Actor watched = _target!;
                Player self = explore.Player;
                int there = Safe(() => watched.MapID, -1);
                int here = self != null ? Safe(() => self.MapID, -1) : -1;
                if (there >= 0 && here >= 0 && there != here) Travel(explore, there, here);
            }

            AfterTravel(explore, free);

            if (free && Plugin.LoadSurroundings.Value && Time.unscaledTime > _travelUntil)
            {
                Actor watched = _target!;
                Player self = explore.Player;
                // Only once both are on the same map: the door case above comes first.
                if (self != null && Safe(() => watched.MapID, -1) == Safe(() => self.MapID, -2))
                    LoadArea(Safe(() => watched.AreaID, -1));
            }

            if (Plugin.DebugInfo.Value == false || Time.unscaledTime - _lastStatus < 2f) return;
            _lastStatus = Time.unscaledTime;

            Actor actor = _target!;
            Camera camera = explore._camera;
            Transform rig = tracking._followBodyTPS;
            Player player = explore.Player;
            Plugin.Logger.LogInfo(
                $"[spectate] {_targetName}: map {Safe(() => actor.MapID, -1)} area {Safe(() => actor.AreaID, -1)}" +
                $" shown {Safe(() => actor.CharacterGrid.Visible, false)}  look at {look.position}" +
                $"  camera {Vector3.Distance(camera.transform.position, look.position):F2} m away" +
                $"  rig target {(rig != null ? Vector3.Distance(rig.position, look.position).ToString("F2") : "?")} m off" +
                $"  mode {tracking.State}  player at {(player != null ? player.Transform.position.ToString() : "?")}" +
                $" map {Safe(() => player.MapID, -1)}  characters shown {ShownCount(explore)}");
        }

        /// <summary>
        /// Once a map change is over: the game has put the player's character at the door of the
        /// new map, so put them away again there. True on the frame that happens.
        /// </summary>
        private bool AfterTravel(ExploreScene explore, bool free)
        {
            if (_sleepAgain == false || free == false || Time.unscaledTime <= _travelUntil) return false;
            _sleepAgain = false;

            Player again = explore.Player;
            if (again == null) return false;

            try { again.Sleep(); } catch { }
            _selfHome = again.Transform.position;
            Plugin.Debug($"[spectate] after the map change the player is at {again.Transform.position}; put away again");
            return true;
        }

        private static int ShownCount(ExploreScene explore)
        {
            var actors = explore._actors;
            if (actors == null) return 0;
            int shown = 0;
            for (int i = 0; i < actors.Count; i++)
            {
                Actor actor = actors[i];
                if (actor != null && Safe(() => actor.CharacterGrid.Visible, false)) shown++;
            }
            return shown;
        }

        /// <summary>
        /// Gives the camera back to the player's character. <paramref name="gameTook"/> is true when
        /// the game itself took the camera for an event or a talk: then the game also decides when
        /// the player's character is drawn, as it would without this plugin.
        /// </summary>
        private void Stop(ExploreScene explore, TrackingCamera tracking, string reason, bool gameTook = false)
        {
            EndFlying(tracking);

            Player player = explore.Player;
            Transform? look = player != null ? Safe(() => LookOf(player), null) : null;
            if (look != null) tracking.Follow = look;
            else Plugin.Logger.LogWarning("Could not find the player's look point: the camera may stay where it is.");

            Plugin.Debug($"[spectate] back to the player: {reason}. While watching, the game re-pointed the camera {_putBack} times." +
                         $" Player at {(player != null ? player.Transform.position.ToString() : "?")} map {(player != null ? Safe(() => player.MapID, -1) : -1)}");
            Note("Back to your character");
            ReleaseHold(tracking);
            BringSelfBack(player, gameTook == false);
            if (player != null && Plugin.LoadSurroundings.Value)
            {
                _lastArea = -100f;
                try { LoadArea(player.AreaID); } catch { }
            }
            Forget();
        }

        private void Forget()
        {
            // The overworld went away under the free camera: nothing to restore, just forget it.
            _flying = false;
            Hooks.Flying = false;
            _flyDestination = -1;
            _hoverActor = null;
            _hoverDoor = null;
            _hoverText = "";

            DropAnchor();
            MapHome();
            if (_stats != null)
            {
                // The panel goes back to showing nobody; the game fills it in again when it wants to.
                try { _stats.Target = null; } catch { }
            }
            Hooks.BlockInteraction = false;
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
            _selfHidden = false;
            _sleepAgain = false;
            Hooks.HideSelf = false;
            _travelUntil = -100f;
        }

        // ---------------------------------------------------------------------------------------
        // On screen
        // ---------------------------------------------------------------------------------------

        private void Note(string text)
        {
            _note = text;
            _noteUntil = Time.unscaledTime + 3f;
        }

        private static string Path(Transform transform)
        {
            string path = transform.name;
            Transform parent = transform.parent;
            for (int i = 0; i < 2 && parent != null; i++)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private void OnGUI()
        {
            HoldAgain();

            string text = "";
            if (Time.unscaledTime < _noteUntil) text = _note;
            else if (_target != null && Plugin.ShowName.Value) text = "Watching " + _targetName;
            else if (_flying && Plugin.ShowName.Value) text = _hoverText.Length > 0 ? "Free camera   -   " + _hoverText : "Free camera";
            if (text.Length == 0) return;

            // Plain labels with a drop shadow: the same calls AC_ADVFreeCamera uses, known to work
            // in this game.
            GUI.color = Color.black;
            GUI.Label(new Rect(21f, 21f, 900f, 24f), text);
            GUI.color = Color.white;
            GUI.Label(new Rect(20f, 20f, 900f, 24f), text);
        }
    }
}
