using System.Runtime.InteropServices;
using System.Text;
using Valve.VR;

namespace EveDeck.VrSpike;

// Point a controller at a panel, squeeze trigger or grip, and the panel follows your hand until you
// let go. Push the thumbstick up/down while holding to resize it.
//
// This manipulates EveDeck's OWN overlays only. Nothing here sends input to an EVE client, which is
// the line the app must not cross.
//
// Aiming uses the controller's "tip" render-model component, NOT the raw device pose. The raw pose
// is the GRIP pose -- on Touch controllers its -Z runs along the handle, tilted well away from where
// you think you are pointing, which makes target selection feel random. The tip component is the
// pose SteamVR itself aims with.
//
// Dragging re-parents the overlay to the controller for the duration of the grab rather than
// recomputing a world transform each frame, which would drift and jitter against pose prediction.
// On release it converts back to a world transform exactly once.
internal sealed class VrManipulator : IDisposable
{
    private const float MinWidth = 0.15f;
    private const float MaxWidth = 8.0f;
    private const float ResizeRatePerSecond = 1.6f;
    private const float StickDeadzone = 0.25f;

    private const float CursorDiameter = 0.05f;

    private static readonly ulong TriggerMask = 1ul << (int)EVRButtonId.k_EButton_SteamVR_Trigger;
    private static readonly ulong GripMask = 1ul << (int)EVRButtonId.k_EButton_Grip;

    private sealed class Hand
    {
        public ulong Grabbed;
        public uint DeviceIndex;
        public bool WasHeld;
        public ulong Laser;
        public ulong Hovered;
        public string Label = "";
    }

    private readonly Hand _left = new() { Label = "left" };
    private readonly Hand _right = new() { Label = "right" };
    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private readonly Dictionary<ulong, bool> _dimmed = [];
    private readonly Action<string>? _log;
    private readonly bool _showLaser;
    private readonly float _pitchDegrees;
    private CVROverlay? _ov;
    private readonly HashSet<uint> _diagnosed = [];

    private readonly Func<ulong, string?>? _keyFor;
    private readonly Action<string, float, float, float, float>? _onDrop;

    public VrManipulator(Action<string>? log = null, bool showLaser = true, float pitchDegrees = 0f,
        Func<ulong, string?>? keyFor = null, Action<string, float, float, float, float>? onDrop = null)
    {
        _log = log;
        _showLaser = showLaser;
        _pitchDegrees = pitchDegrees;
        _keyFor = keyFor;
        _onDrop = onDrop;
    }

    // Rotation about the controller's local X axis. Touch drivers that expose no "tip" component
    // leave the aim ray running along the handle; a negative pitch tilts it forward to match where
    // a finger actually points.
    private static HmdMatrix34_t PitchMatrix(float degrees)
    {
        var r = degrees * MathF.PI / 180f;
        var c = MathF.Cos(r);
        var s = MathF.Sin(r);
        return new HmdMatrix34_t
        {
            m0 = 1, m1 = 0, m2 = 0, m3 = 0,
            m4 = 0, m5 = c, m6 = -s, m7 = 0,
            m8 = 0, m9 = s, m10 = c, m11 = 0,
        };
    }

    public bool Busy => _left.Grabbed != 0 || _right.Grabbed != 0;

    public void Update(CVROverlay ov, IReadOnlyList<ulong> panelHandles, float deltaSeconds)
    {
        _ov = ov;
        var system = OpenVR.System;
        if (system is null) return;

        system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, _poses);

        UpdateHand(ov, system, _left, ETrackedControllerRole.LeftHand, panelHandles, deltaSeconds);
        UpdateHand(ov, system, _right, ETrackedControllerRole.RightHand, panelHandles, deltaSeconds);

        ApplyHighlight(ov, panelHandles);
    }

    private void UpdateHand(CVROverlay ov, CVRSystem system, Hand hand, ETrackedControllerRole role,
        IReadOnlyList<ulong> panelHandles, float deltaSeconds)
    {
        var index = system.GetTrackedDeviceIndexForControllerRole(role);
        if (index == OpenVR.k_unTrackedDeviceIndexInvalid || index >= _poses.Length)
        {
            hand.Grabbed = 0;
            hand.Hovered = 0;
            HideCursor(ov, hand);
            return;
        }

        var pose = _poses[index];
        if (!pose.bPoseIsValid || !pose.bDeviceIsConnected)
        {
            hand.Grabbed = 0;
            hand.Hovered = 0;
            HideCursor(ov, hand);
            return;
        }

        var state = default(VRControllerState_t);
        var size = (uint)Marshal.SizeOf<VRControllerState_t>();
        if (!system.GetControllerState(index, ref state, size)) return;

        var aim = AimPose(system, index, pose.mDeviceToAbsoluteTracking);

        var held = (state.ulButtonPressed & (TriggerMask | GripMask)) != 0;

        if (held && !hand.WasHeld)
        {
            TryGrab(ov, hand, index, aim, panelHandles);
        }
        else if (!held && hand.WasHeld && hand.Grabbed != 0)
        {
            Release(ov, hand, pose.mDeviceToAbsoluteTracking);
        }
        else if (held && hand.Grabbed != 0)
        {
            Resize(ov, hand, state, deltaSeconds);
        }

        // Hover feedback only matters when a hand is free.
        if (hand.Grabbed != 0)
        {
            hand.Hovered = hand.Grabbed;
            HideCursor(ov, hand);
        }
        else
        {
            hand.Hovered = Pick(ov, aim, panelHandles, out var hit);
            if (hand.Hovered != 0) ShowCursor(ov, hand, hit);
            else HideCursor(ov, hand);
        }

        hand.WasHeld = held;
    }

    // Device pose -> aim pose, via the render model's "tip" component when the driver exposes one.
    private HmdMatrix34_t AimPose(CVRSystem system, uint index, HmdMatrix34_t devicePose)
    {
        try
        {
            var renderModels = OpenVR.RenderModels;
            if (renderModels is null) return devicePose;

            var error = ETrackedPropertyError.TrackedProp_Success;
            var name = new StringBuilder(256);
            system.GetStringTrackedDeviceProperty(index, ETrackedDeviceProperty.Prop_RenderModelName_String,
                name, (uint)name.Capacity, ref error);
            if (error != ETrackedPropertyError.TrackedProp_Success || name.Length == 0)
            {
                Diagnose(index, $"render model name unavailable ({error}) -- aiming from grip pose");
                return Corrected(devicePose);
            }

            var controllerState = default(VRControllerState_t);
            var modeState = default(RenderModel_ControllerMode_State_t);
            var componentState = default(RenderModel_ComponentState_t);

            if (!renderModels.GetComponentState(name.ToString(), OpenVR.k_pch_Controller_Component_Tip,
                    ref controllerState, ref modeState, ref componentState))
            {
                Diagnose(index, $"model \"{name}\" exposes no \"{OpenVR.k_pch_Controller_Component_Tip}\" component -- aiming from grip pose. Use --aim-pitch=<deg> (try -40).");
                return Corrected(devicePose);
            }

            var tip = componentState.mTrackingToComponentLocal;
            var f = VrMath.Forward(tip);
            Diagnose(index, $"model \"{name}\" tip component OK; local forward {f.v0:F2},{f.v1:F2},{f.v2:F2}");
            return Corrected(VrMath.Multiply(devicePose, tip));
        }
        catch (Exception ex)
        {
            Diagnose(index, $"tip lookup threw ({ex.GetType().Name}) -- aiming from grip pose");
            return Corrected(devicePose);
        }
    }

    private HmdMatrix34_t Corrected(HmdMatrix34_t pose) =>
        _pitchDegrees == 0f ? pose : VrMath.Multiply(pose, PitchMatrix(_pitchDegrees));

    // One line per device, so the console says what the driver actually offered instead of leaving
    // a wrong ray to be guessed at.
    private void Diagnose(uint index, string message)
    {
        if (!_diagnosed.Add(index)) return;
        _log?.Invoke($"aim[{index}]: {message}");
    }

    private void TryGrab(CVROverlay ov, Hand hand, uint index, HmdMatrix34_t aim, IReadOnlyList<ulong> panelHandles)
    {
        var target = Pick(ov, aim, panelHandles);
        if (target == 0) return;

        var origin = ETrackingUniverseOrigin.TrackingUniverseStanding;
        var overlayWorld = VrMath.Identity;
        if (ov.GetOverlayTransformAbsolute(target, ref origin, ref overlayWorld) != EVROverlayError.None) return;

        // Offset is captured against the DEVICE pose, because that is what the overlay will be
        // parented to -- mixing in the tip correction here would shift the panel on pickup.
        var device = _poses[index].mDeviceToAbsoluteTracking;
        var offset = VrMath.Multiply(VrMath.Invert(device), overlayWorld);
        if (ov.SetOverlayTransformTrackedDeviceRelative(target, index, ref offset) != EVROverlayError.None) return;

        hand.Grabbed = target;
        hand.DeviceIndex = index;
    }

    private void Release(CVROverlay ov, Hand hand, HmdMatrix34_t device)
    {
        var relativeOrigin = default(uint);
        var offset = VrMath.Identity;
        if (ov.GetOverlayTransformTrackedDeviceRelative(hand.Grabbed, ref relativeOrigin, ref offset) == EVROverlayError.None)
        {
            var world = VrMath.Multiply(device, offset);
            ov.SetOverlayTransformAbsolute(hand.Grabbed, ETrackingUniverseOrigin.TrackingUniverseStanding, ref world);

            var (x, y, z) = VrMath.Position(world);
            var width = 0f;
            ov.GetOverlayWidthInMeters(hand.Grabbed, ref width);

            var key = _keyFor?.Invoke(hand.Grabbed);
            if (!string.IsNullOrEmpty(key))
            {
                _onDrop?.Invoke(key, x, y, z, width);
                _log?.Invoke($"drop: seat {key} at {x:F2},{y:F2},{z:F2} width {width:F2}m (saved)");
            }
            else
            {
                _log?.Invoke($"drop: {x:F2},{y:F2},{z:F2} width {width:F2}m");
            }
        }

        hand.Grabbed = 0;
    }

    private static void Resize(CVROverlay ov, Hand hand, VRControllerState_t state, float deltaSeconds)
    {
        // Legacy bindings put the thumbstick on a different axis index per controller, so take
        // whichever axis is actually deflected rather than assuming rAxis0. Trigger and grip report
        // on .x, so reading .y keeps them from being mistaken for a stick.
        var stick = 0f;
        foreach (var axis in new[] { state.rAxis0.y, state.rAxis1.y, state.rAxis2.y, state.rAxis3.y, state.rAxis4.y })
            if (MathF.Abs(axis) > MathF.Abs(stick)) stick = axis;

        if (MathF.Abs(stick) < StickDeadzone) return;

        var width = 0f;
        if (ov.GetOverlayWidthInMeters(hand.Grabbed, ref width) != EVROverlayError.None || width <= 0f) return;

        var scaled = width * (1f + (stick * ResizeRatePerSecond * deltaSeconds));
        ov.SetOverlayWidthInMeters(hand.Grabbed, Math.Clamp(scaled, MinWidth, MaxWidth));
    }

    private static ulong Pick(CVROverlay ov, HmdMatrix34_t aim, IReadOnlyList<ulong> panelHandles) =>
        Pick(ov, aim, panelHandles, out _);

    private static ulong Pick(CVROverlay ov, HmdMatrix34_t aim, IReadOnlyList<ulong> panelHandles, out HmdVector3_t hit)
    {
        var best = 0ul;
        var bestDistance = float.MaxValue;
        hit = default;

        var parameters = new VROverlayIntersectionParams_t
        {
            eOrigin = ETrackingUniverseOrigin.TrackingUniverseStanding,
            vSource = VrMath.Origin(aim),
            vDirection = VrMath.Forward(aim),
        };

        foreach (var handle in panelHandles)
        {
            var results = default(VROverlayIntersectionResults_t);
            if (!ov.ComputeOverlayIntersection(handle, ref parameters, ref results)) continue;
            if (results.fDistance >= bestDistance) continue;

            bestDistance = results.fDistance;
            best = handle;
            hit = results.vPoint;
        }

        return best;
    }

    // Dim every panel except the one currently under a laser, so the target is unambiguous.
    private void ApplyHighlight(CVROverlay ov, IReadOnlyList<ulong> panelHandles)
    {
        var hovered = new HashSet<ulong>();
        if (_left.Hovered != 0) hovered.Add(_left.Hovered);
        if (_right.Hovered != 0) hovered.Add(_right.Hovered);

        var anyHover = hovered.Count > 0;

        foreach (var handle in panelHandles)
        {
            // No hover at all means normal brightness everywhere -- never leave panels dimmed.
            var shouldDim = anyHover && !hovered.Contains(handle);
            if (_dimmed.TryGetValue(handle, out var already) && already == shouldDim) continue;

            _dimmed[handle] = shouldDim;
            if (shouldDim) ov.SetOverlayColor(handle, 0.45f, 0.45f, 0.5f);
            else ov.SetOverlayColor(handle, 1f, 1f, 1f);
        }
    }

    // A small disc parked at the ray's hit point, turned to face the viewer. A beam was the obvious
    // first choice and the wrong one: a flat quad lying ALONG the aim ray is edge-on exactly when
    // you sight down it, so it looks broken or absent. A cursor is unambiguous from any angle.
    private void ShowCursor(CVROverlay ov, Hand hand, HmdVector3_t hit)
    {
        if (!_showLaser) return;

        if (hand.Laser == 0)
        {
            ulong handle = 0;
            if (ov.CreateOverlay($"evedeck.vrspike.cursor.{hand.Label}", $"Cursor {hand.Label}", ref handle) != EVROverlayError.None)
                return;

            // Uploaded once and never updated -- it is SetOverlayRaw in a LOOP that leaks.
            const int size = 32;
            var pixels = new byte[size * size * 4];
            var centre = (size - 1) / 2f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - centre;
                    var dy = y - centre;
                    var dist = MathF.Sqrt((dx * dx) + (dy * dy)) / centre;
                    var i = ((y * size) + x) * 4;
                    // Soft-edged disc so it reads cleanly against bright client pixels.
                    var alpha = dist >= 1f ? 0f : MathF.Min(1f, (1f - dist) * 3f);
                    pixels[i + 0] = 140;
                    pixels[i + 1] = 215;
                    pixels[i + 2] = 255;
                    pixels[i + 3] = (byte)(alpha * 255);
                }
            }

            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                ov.SetOverlayRaw(handle, pinned.AddrOfPinnedObject(), size, size, 4);
            }
            finally
            {
                pinned.Free();
            }

            ov.SetOverlayWidthInMeters(handle, CursorDiameter);
            ov.SetOverlayAlpha(handle, 0.95f);
            // Draw on top of the panel it is sitting on, not z-fighting with it.
            ov.SetOverlaySortOrder(handle, 100);
            hand.Laser = handle;
        }

        var hmd = VrMath.Origin(_poses[OpenVR.k_unTrackedDeviceIndex_Hmd].mDeviceToAbsoluteTracking);
        var transform = VrMath.FaceToward(hit, hmd);
        ov.SetOverlayTransformAbsolute(hand.Laser, ETrackingUniverseOrigin.TrackingUniverseStanding, ref transform);
        ov.ShowOverlay(hand.Laser);
    }

    private static void HideCursor(CVROverlay ov, Hand hand)
    {
        if (hand.Laser != 0) ov.HideOverlay(hand.Laser);
    }

    public void Dispose()
    {
        if (_ov is null) return;
        foreach (var hand in new[] { _left, _right })
        {
            if (hand.Laser != 0) _ov.DestroyOverlay(hand.Laser);
            hand.Laser = 0;
        }
    }
}
