// BoardBeaconMod.cs
//
// Purpose:
//   Example “consumer mod” that uses VS.ModBridge to get a continuous player/board snapshot,
//   then renders an in-world visual (a vertical cylinder “beacon”) over the board when
//   certain conditions are met.
//
// Key idea:
//   We do NOT directly read or hook game internals here.
//   We rely on VS.ModBridge (VSBridge) to abstract all that complexity and provide VSSnapshot.
//
// Why this is a good VSBridge example:
//   - Uses VSBridge.Ensure() to guarantee the bridge exists.
//   - Subscribes to VSBridge.OnSnapshot to get consistent, centralized state.
//   - Makes a gameplay feature (beacon) entirely from snapshot data + Unity primitives.
//   - Keeps settings in an external config file (no rebuild needed).
//
// Notes:
//   - This mod deliberately keeps its own logic simple and “consumer-friendly”.
//   - The heavy lifting (reflection, XR input reads, board pose resolution) happens in the bridge.

using MelonLoader;
using MelonLoader.Utils;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;
using VS.ModBridge; // <- IMPORTANT: This is the dependency we’re demonstrating
using Object = UnityEngine.Object;

// MelonLoader metadata for the mod.
[assembly: MelonInfo(typeof(BoardBeaconMod.BoardBeaconEntry), "VS Board Beacon", "1.1.0", "Josh2367")]

// Example Mod for VS Skate
namespace BoardBeaconMod
{
    public class BoardBeaconEntry : MelonMod
    {
        // -------------------------
        // Configurable settings
        // -------------------------
        //
        // These are “tunables” that affect behavior and visuals.
        // We keep them as static fields so:
        //   - the values are easy to update from config
        //   - the values are globally consistent for this mod
        //
        // This mod also hot-reloads the config file while running.

        // Hand must be at least this far from headset (meters) to count as “arm extended”.
        // Why:
        //   We want a “deliberate gesture” so the beacon doesn’t flicker when casually gripping.
        static float ArmExtendedMeters = 0.55f;

        // Consider grip “active” if analog grip exceeds this threshold (0..1).
        // Why:
        //   Different headsets/controllers can have noisy grip signals; threshold stabilizes it.
        static float AnalogGripThreshold = 0.60f;

        // If the board is already close enough to see naturally, suppress the beacon.
        // Why:
        //   Prevents visual clutter and avoids “beacon spam” in normal riding flow.
        static float MinBoardDistanceForBeacon = 4.0f;

        // Beacon visuals (purely cosmetic)
        static float BeaconHeight = 180f; // world-space height (very tall so it’s visible at distance)
        static float BeaconRadius = 0.08f; // cylinder thickness
        static Color BeaconColor = new Color(0.0f, 1.0f, 0.2f, 0.85f); // RGBA

        // -------------------------
        // Runtime state
        // -------------------------
        //
        // We create Unity objects once and then simply move/enable them.
        // This avoids allocations and repeated primitive creation.

        GameObject _beaconRoot;     // Parent object for the beacon (easy enable/disable)
        GameObject _beamGO;         // The actual cylinder primitive we scale/color
        MeshRenderer _beaconRenderer;
        Material _beaconMat;

        // We track whether we’ve subscribed to VSBridge.OnSnapshot so we can cleanly unsubscribe.
        bool _subscribed;

        // Config path and last-write time for hot reload.
        string _cfgPath;
        DateTime _cfgLastWriteUtc;

        // -------------------------
        // MelonLoader lifecycle
        // -------------------------

        public override void OnInitializeMelon()
        {
            // 1) Ensure the bridge exists.
            //
            // Why:
            //   The bridge is responsible for resolving game managers and sampling state.
            //   If your consumer mod calls Ensure() early, it guarantees snapshots will start.
            //
            // This is the “clean dependency boundary”: consumer mods do NOT replicate bridge internals.
            VSBridge.Ensure();

            // 2) Setup config.
            // We store the config alongside the mod DLL so it’s easy for users to find.
            _cfgPath = GetConfigPath();
            EnsureConfigFileExists();
            LoadConfig(); // load + apply immediately (so visuals/logic use user prefs)

            // 3) Subscribe to the bridge snapshot event.
            SubscribeSnapshot();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // Scenes can destroy Unity references, or object graphs can change.
            // So we rebuild the beacon on scene load to avoid stale references.
            //
            // This is a common pattern for Melon/Unity mods:
            //   - Destroy runtime objects
            //   - Recreate them in the new scene context
            DestroyBeacon();
            CreateBeaconIfNeeded();
            ApplyBeaconVisuals(); // reapply config to fresh objects
        }

        public override void OnUpdate()
        {
            // Hot reload is intentionally done here:
            // - low complexity
            // - safe for file IO checks (as long as we keep it lightweight)
            //
            // We only reload when the file timestamp changes, not every frame.
            TryReloadConfigIfChanged();
        }

        public override void OnDeinitializeMelon()
        {
            // Always unsubscribe from events.
            // Why:
            //   If you don’t, you risk dangling callbacks and errors during shutdown/reload.
            UnsubscribeSnapshot();

            // Clean up Unity objects we created.
            DestroyBeacon();
        }

        // -------------------------
        // Snapshot handling (VSBridge consumer)
        // -------------------------
        //
        // VSBridge publishes snapshots that contain:
        //   - headset + hands poses
        //   - controller input states (including analog grip)
        //   - board pose (ActualWorld, plus other targets)
        //   - player state (OffBoard/Riding/etc)
        //
        // This mod uses only a small part of that data:
        //   PlayerState + analog grip + headset/hand positions + board ActualWorld.

        void SubscribeSnapshot()
        {
            if (_subscribed) return;

            // Subscribe once; we don’t want multiple duplicate subscriptions.
            VSBridge.OnSnapshot += OnSnapshot;
            _subscribed = true;
        }

        void UnsubscribeSnapshot()
        {
            if (!_subscribed) return;

            VSBridge.OnSnapshot -= OnSnapshot;
            _subscribed = false;
        }

        // Called by VSBridge at its sampling cadence (not necessarily every Unity frame).
        void OnSnapshot(in VSSnapshot s)
        {
            // Ensure the beacon exists before we try to manipulate it.
            // We lazily create it because:
            //   - it avoids doing Unity object creation too early
            //   - it survives odd init ordering and scene timing
            if (_beaconRoot == null) CreateBeaconIfNeeded();
            if (_beaconRoot == null) return; // if creation failed, do nothing safely

            // 1) We only show the beacon when the player is off the board.
            // Why:
            //   Beacon is meant for “where did my board go?” scenarios.
            bool offBoard = (s.PlayerState == VSPlayerState.OffBoard);

            // 2) Grip detection (analog):
            // We consider either hand’s grip sufficient.
            // Why:
            //   Users might use left or right hand depending on preference.
            bool gripHeld =
                (s.Buttons.GripLeft >= AnalogGripThreshold) ||
                (s.Buttons.GripRight >= AnalogGripThreshold);

            // 3) Arm extension gating:
            // We measure distance from headset to each hand pose and require:
            //   grip on that hand AND arm distance >= ArmExtendedMeters
            //
            // Why:
            //   Prevents beacon from showing when hands are near the body (accidental grip).
            //   Requiring “extended” makes it intentional and reduces flicker.
            float dLeft = Vector3.Distance(s.LeftHand.position, s.Headset.position);
            float dRight = Vector3.Distance(s.RightHand.position, s.Headset.position);

            bool leftIsGrip = (s.Buttons.GripLeft >= AnalogGripThreshold);
            bool rightIsGrip = (s.Buttons.GripRight >= AnalogGripThreshold);

            bool armExtended =
                (leftIsGrip && dLeft >= ArmExtendedMeters) ||
                (rightIsGrip && dRight >= ArmExtendedMeters);

            // Combined “should show beacon” condition.
            // If any part fails, hide and return.
            bool shouldShow = offBoard && gripHeld && armExtended;
            if (!shouldShow)
            {
                SetBeaconVisible(false);
                return;
            }

            // 4) Board pose:
            // We use the “true in-world board pose” provided by VSBridge.
            // Why:
            //   This is exactly what a consumer mod *should* do: use the bridge’s “best” board target.
            Vector3 boardPos = s.Board.ActualWorld.position;

            // 5) Distance gate:
            // If the board is already near the headset, suppress the beacon.
            // Why:
            //   When the board is close, the player can usually see it without a beacon.
            float boardDist = Vector3.Distance(s.Headset.position, boardPos);
            if (boardDist < MinBoardDistanceForBeacon)
            {
                SetBeaconVisible(false);
                return;
            }

            // 6) Position the beacon:
            // We use a cylinder scaled to BeaconHeight * 0.5 in Y.
            // A Unity cylinder primitive is centered at its origin, so we offset the root upward
            // by half its height to make it “stand on” the board.
            Vector3 beaconPos = boardPos + Vector3.up * (BeaconHeight * 0.5f);

            _beaconRoot.transform.position = beaconPos;

            // Keep it world-aligned (not tilted).
            // Why:
            //   We want a consistent vertical beacon regardless of board rotation.
            _beaconRoot.transform.rotation = Quaternion.identity;

            // Finally, show it.
            SetBeaconVisible(true);
        }

        // -------------------------
        // Beacon creation + visuals
        // -------------------------

        void CreateBeaconIfNeeded()
        {
            if (_beaconRoot != null) return;

            // Root object:
            // We mark it DontDestroyOnLoad so it survives scene loads.
            // Note:
            //   We still explicitly destroy + recreate on scene load, but DontDestroyOnLoad
            //   protects us from transient scene cleanup and keeps behavior predictable.
            _beaconRoot = new GameObject("VS_BoardBeacon");
            Object.DontDestroyOnLoad(_beaconRoot);

            // Beam primitive:
            // A cylinder is a fast, built-in mesh with a renderer.
            _beamGO = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _beamGO.name = "Beam";
            _beamGO.transform.SetParent(_beaconRoot.transform, false);

            // Remove collider:
            // Why:
            //   This is a purely visual indicator. Colliders could cause physics interactions,
            //   raycast hits, or unintended gameplay side-effects.
            var col = _beamGO.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);

            _beaconRenderer = _beamGO.GetComponent<MeshRenderer>();

            // Choose an unlit shader:
            // Why:
            //   Beacons should be visible regardless of scene lighting.
            // Unlit/Color is ideal; Sprites/Default is a fallback that’s often present.
            Shader unlit = Shader.Find("Unlit/Color");
            if (unlit == null)
                unlit = Shader.Find("Sprites/Default");

            // Create a unique material:
            // Why:
            //   We want to control color without affecting shared materials used by other objects.
            _beaconMat = new Material(unlit);
            if (_beaconRenderer != null)
                _beaconRenderer.sharedMaterial = _beaconMat;

            // Apply user-configured visuals to this newly created object.
            ApplyBeaconVisuals();

            // Start hidden by default.
            SetBeaconVisible(false);
        }

        void ApplyBeaconVisuals()
        {
            // Scale:
            // Unity cylinder default height is 2 units (from -1 to +1 on Y),
            // and scaling Y scales half-height behavior accordingly.
            //
            // Here we set localScale Y to (BeaconHeight * 0.5f) so the visible height becomes BeaconHeight.
            if (_beamGO != null)
            {
                _beamGO.transform.localScale = new Vector3(BeaconRadius, BeaconHeight * 0.5f, BeaconRadius);
            }

            // Color:
            // Using an unlit material means this is “always visible” and consistent.
            if (_beaconMat != null)
            {
                _beaconMat.color = BeaconColor;
            }
        }

        void SetBeaconVisible(bool on)
        {
            // We toggle the root GameObject active instead of enabling/disabling renderer only.
            // Why:
            //   This keeps it simple: everything under the root turns on/off consistently.
            if (_beaconRoot != null)
                _beaconRoot.SetActive(on);
        }

        void DestroyBeacon()
        {
            // Always destroy created materials:
            // Why:
            //   Unity materials are native engine objects; leaving them can leak memory across reloads.
            if (_beaconMat != null)
            {
                Object.Destroy(_beaconMat);
                _beaconMat = null;
            }

            // Destroy root (which destroys children too).
            if (_beaconRoot != null)
            {
                Object.Destroy(_beaconRoot);
                _beaconRoot = null;
                _beamGO = null;
                _beaconRenderer = null;
            }
        }

        // -------------------------
        // Config file (same folder as DLL)
        // -------------------------
        //
        // Design goals:
        //   - Config lives next to the mod DLL for easy discovery.
        //   - File is created with defaults if missing.
        //   - User can edit while game is running; we hot-reload on timestamp change.
        //
        // This is intentionally “simple INI-ish” parsing:
        //   key=value, ignore blanks and # / // comments.

        static string GetConfigPath()
        {
            // Assembly.GetExecutingAssembly().Location is the full path to this mod’s DLL.
            // We put the config next to it for convenience.
            string dllPath = Assembly.GetExecutingAssembly().Location;
            string dir = Path.GetDirectoryName(dllPath) ?? MelonEnvironment.ModsDirectory;
            return Path.Combine(dir, "VSBoardBeacon.cfg");
        }

        void EnsureConfigFileExists()
        {
            try
            {
                if (File.Exists(_cfgPath))
                {
                    // Track last-write so we can detect edits later.
                    _cfgLastWriteUtc = File.GetLastWriteTimeUtc(_cfgPath);
                    return;
                }

                // Ensure directory exists and write defaults.
                Directory.CreateDirectory(Path.GetDirectoryName(_cfgPath) ?? ".");
                File.WriteAllText(_cfgPath, GetDefaultConfigText());
                _cfgLastWriteUtc = File.GetLastWriteTimeUtc(_cfgPath);

                MelonLogger.Msg($"[BoardBeacon] Created config: {_cfgPath}");
            }
            catch (Exception ex)
            {
                // Don’t hard-fail the mod if config creation fails.
                // The mod can still run with baked-in defaults.
                MelonLogger.Warning($"[BoardBeacon] Failed to create config: {ex.Message}");
            }
        }

        void TryReloadConfigIfChanged()
        {
            try
            {
                if (string.IsNullOrEmpty(_cfgPath) || !File.Exists(_cfgPath))
                    return;

                // Only reload if file timestamp advanced.
                // Why:
                //   Avoid constant disk reads every frame.
                var t = File.GetLastWriteTimeUtc(_cfgPath);
                if (t <= _cfgLastWriteUtc)
                    return;

                _cfgLastWriteUtc = t;
                LoadConfig();
            }
            catch
            {
                // Intentionally ignore:
                // - If file is temporarily locked mid-save, or timestamp read fails,
                //   we simply try again later without spamming logs.
            }
        }

        void LoadConfig()
        {
            // Start from defaults, then override from file.
            //
            // Why:
            //   This is resilient. If the user deletes a key or writes a partial file,
            //   you still get safe defaults for missing values.

            float arm = 0.55f;
            float grip = 0.60f;
            float minDist = 4.0f;

            float height = 180f;
            float radius = 0.08f;
            Color col = new Color(0.0f, 1.0f, 0.2f, 0.85f);

            try
            {
                if (!File.Exists(_cfgPath))
                {
                    EnsureConfigFileExists();
                }

                // Read file line-by-line:
                // - ignore blank lines
                // - ignore comments (# or //)
                // - split on first '='
                string[] lines = File.ReadAllLines(_cfgPath);
                foreach (var raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("#") || line.StartsWith("//")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    // Case-insensitive key matching:
                    // Why:
                    //   Makes config more forgiving for users editing by hand.
                    if (key.Equals("ArmExtendedMeters", StringComparison.OrdinalIgnoreCase))
                        TryParseFloat(val, ref arm);
                    else if (key.Equals("AnalogGripThreshold", StringComparison.OrdinalIgnoreCase))
                        TryParseFloat(val, ref grip);
                    else if (key.Equals("MinBoardDistanceForBeacon", StringComparison.OrdinalIgnoreCase))
                        TryParseFloat(val, ref minDist);
                    else if (key.Equals("BeaconHeight", StringComparison.OrdinalIgnoreCase))
                        TryParseFloat(val, ref height);
                    else if (key.Equals("BeaconRadius", StringComparison.OrdinalIgnoreCase))
                        TryParseFloat(val, ref radius);
                    else if (key.Equals("BeaconColor", StringComparison.OrdinalIgnoreCase))
                        TryParseColor(val, ref col);
                }

                // Sanity clamps:
                // Why:
                //   Config files are user-editable. Clamp values to avoid nonsensical or broken visuals.
                arm = Mathf.Clamp(arm, 0.10f, 2.50f);
                grip = Mathf.Clamp01(grip);
                minDist = Mathf.Max(0f, minDist);

                height = Mathf.Clamp(height, 1f, 1000f);
                radius = Mathf.Clamp(radius, 0.01f, 2f);
                col.r = Mathf.Clamp01(col.r);
                col.g = Mathf.Clamp01(col.g);
                col.b = Mathf.Clamp01(col.b);
                col.a = Mathf.Clamp01(col.a);

                // Commit to the actual static fields used by runtime logic.
                ArmExtendedMeters = arm;
                AnalogGripThreshold = grip;
                MinBoardDistanceForBeacon = minDist;

                BeaconHeight = height;
                BeaconRadius = radius;
                BeaconColor = col;

                // Apply visuals immediately if beacon exists.
                ApplyBeaconVisuals();

                // Log a compact summary for debugging / user support.
                MelonLogger.Msg(
                    $"[BoardBeacon] Config loaded: ArmExtendedMeters={ArmExtendedMeters:F2}, " +
                    $"AnalogGripThreshold={AnalogGripThreshold:F2}, MinBoardDistanceForBeacon={MinBoardDistanceForBeacon:F2}, " +
                    $"BeaconHeight={BeaconHeight:F1}, BeaconRadius={BeaconRadius:F2}, " +
                    $"BeaconColor=({BeaconColor.r:F2},{BeaconColor.g:F2},{BeaconColor.b:F2},{BeaconColor.a:F2})");
            }
            catch (Exception ex)
            {
                // Again: don’t crash if parsing fails; keep whatever last-good values we had.
                MelonLogger.Warning($"[BoardBeacon] Failed to load config '{_cfgPath}': {ex.Message}");
            }
        }

        // Parsing helpers (InvariantCulture avoids decimal comma issues in some locales)
        static bool TryParseFloat(string s, ref float dst)
        {
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            {
                dst = f;
                return true;
            }
            return false;
        }

        static bool TryParseColor(string s, ref Color dst)
        {
            // Accept:
            //   BeaconColor=0,1,0.2,0.85
            //   BeaconColor=0 1 0.2 0.85
            //
            // We split on comma or whitespace and read r,g,b,(optional a).
            char[] seps = new[] { ',', ' ', '\t' };
            var parts = s.Split(seps, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;

            float r = dst.r, g = dst.g, b = dst.b, a = dst.a;

            bool okR = float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r);
            bool okG = float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g);
            bool okB = float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b);

            if (!okR || !okG || !okB) return false;

            if (parts.Length >= 4)
            {
                if (float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float aa))
                    a = aa;
            }

            dst = new Color(r, g, b, a);
            return true;
        }

        static string GetDefaultConfigText()
        {
            // This is the template written when the config file doesn’t exist.
            // It also documents expected formats for users.
            return
@"# VS Board Beacon config
# Edit values and save; the mod hot-reloads changes while running.

# Hand must be at least this far from headset to count as ""arm extended"" (meters).
ArmExtendedMeters=0.55

# Grip analog threshold (0..1) to count as ""grip held"".
AnalogGripThreshold=0.60

# If board is closer than this distance (meters), don't show the beacon.
MinBoardDistanceForBeacon=4.0

# Beacon visuals
BeaconHeight=180
BeaconRadius=0.08

# RGBA (0..1). Format: r,g,b,a
BeaconColor=0.0,1.0,0.2,0.85
";
        }
    }
}
