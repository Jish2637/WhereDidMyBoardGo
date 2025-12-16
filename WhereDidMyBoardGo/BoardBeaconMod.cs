// BoardBeaconMod.cs
//
// Purpose:
//   Example “consumer mod” that uses VS.ModBridge to get a continuous player/board snapshot,
//   then renders an in-world visual (a vertical cylinder “beacon”) over the board when
//   certain conditions are met.
//


using MelonLoader;
using UnityEngine;
using VS.ModBridge;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(BoardBeaconMod.BoardBeaconEntry), "VS Board Beacon", "1.3.0", "Josh2367")]

namespace BoardBeaconMod
{
    public class BoardBeaconEntry : MelonMod
    {
        // -------------------------
        // Hard-coded tunables
        // -------------------------

        // Gesture logic
        static readonly float ArmExtendedMeters = 0.25f;
        static readonly float AnalogGripThreshold = 0.60f;
        static readonly float MinBoardDistanceForBeacon = 3.0f;

        // Beacon visuals
        static readonly float BeaconHeight = 180f;
        static readonly float BeaconRadius = 0.08f;
        static readonly Color BeaconColor = new Color(0.0f, 1.0f, 0.2f, 0.85f);

        // -------------------------
        // Runtime state
        // -------------------------

        GameObject _beaconRoot;
        GameObject _beamGO;
        Material _beaconMat;

        bool _subscribed;

        // -------------------------
        // MelonLoader lifecycle
        // -------------------------

        public override void OnInitializeMelon()
        {
            // Ensure VSBridge exists and starts sampling
            VSBridge.Ensure();

            SubscribeSnapshot();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            DestroyBeacon();
            CreateBeaconIfNeeded();
            ApplyBeaconVisuals();
        }

        public override void OnDeinitializeMelon()
        {
            UnsubscribeSnapshot();
            DestroyBeacon();
        }

        // -------------------------
        // Snapshot handling
        // -------------------------

        void SubscribeSnapshot()
        {
            if (_subscribed) return;
            VSBridge.OnSnapshot += OnSnapshot;
            _subscribed = true;
        }

        void UnsubscribeSnapshot()
        {
            if (!_subscribed) return;
            VSBridge.OnSnapshot -= OnSnapshot;
            _subscribed = false;
        }

        void OnSnapshot(in VSSnapshot s)
        {
            if (_beaconRoot == null)
                CreateBeaconIfNeeded();

            if (_beaconRoot == null)
                return;

            // Only when player is off board
            if (s.PlayerState != VSPlayerState.OffBoard)
            {
                SetBeaconVisible(false);
                return;
            }

            // Grip detection
            bool gripHeld =
                (s.Buttons.GripLeft >= AnalogGripThreshold) ||
                (s.Buttons.GripRight >= AnalogGripThreshold);

            if (!gripHeld)
            {
                SetBeaconVisible(false);
                return;
            }

            // Arm extension gating
            float dLeft = Vector3.Distance(s.LeftHand.position, s.Headset.position);
            float dRight = Vector3.Distance(s.RightHand.position, s.Headset.position);

            bool armExtended =
                (s.Buttons.GripLeft >= AnalogGripThreshold && dLeft >= ArmExtendedMeters) ||
                (s.Buttons.GripRight >= AnalogGripThreshold && dRight >= ArmExtendedMeters);

            if (!armExtended)
            {
                SetBeaconVisible(false);
                return;
            }

            // Board distance check (horizontal only)
            Vector3 boardPos = s.Board.ActualWorld.position;
            Vector3 delta = boardPos - s.Headset.position;
            delta.y = 0f;

            if (delta.magnitude < MinBoardDistanceForBeacon)
            {
                SetBeaconVisible(false);
                return;
            }

            // Position beacon
            _beaconRoot.transform.position =
                boardPos + Vector3.up * (BeaconHeight * 0.5f);

            _beaconRoot.transform.rotation = Quaternion.identity;

            SetBeaconVisible(true);
        }

        // -------------------------
        // Beacon creation + visuals
        // -------------------------

        void CreateBeaconIfNeeded()
        {
            if (_beaconRoot != null) return;

            _beaconRoot = new GameObject("VS_BoardBeacon");
            Object.DontDestroyOnLoad(_beaconRoot);

            _beamGO = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _beamGO.name = "Beam";
            _beamGO.transform.SetParent(_beaconRoot.transform, false);

            // Remove collider (visual only)
            var col = _beamGO.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);

            Shader shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            _beaconMat = new Material(shader);

            var mr = _beamGO.GetComponent<MeshRenderer>();
            if (mr != null)
                mr.sharedMaterial = _beaconMat;

            ApplyBeaconVisuals();
            SetBeaconVisible(false);
        }

        void ApplyBeaconVisuals()
        {
            if (_beamGO != null)
            {
                _beamGO.transform.localScale =
                    new Vector3(BeaconRadius, BeaconHeight * 0.5f, BeaconRadius);
            }

            if (_beaconMat != null)
                _beaconMat.color = BeaconColor;
        }

        void SetBeaconVisible(bool visible)
        {
            if (_beaconRoot != null)
                _beaconRoot.SetActive(visible);
        }

        void DestroyBeacon()
        {
            if (_beaconMat != null)
            {
                Object.Destroy(_beaconMat);
                _beaconMat = null;
            }

            if (_beaconRoot != null)
            {
                Object.Destroy(_beaconRoot);
                _beaconRoot = null;
                _beamGO = null;
            }
        }
    }
}
