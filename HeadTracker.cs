using System;
using System.Numerics;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Hypostasis.Game;

namespace noWickyXIV;

// Per-frame override of Character.LookAt so the head/torso/eyes aim at a
// point projected along the camera's forward vector instead of the current
// target. The previous spike confirmed that writing Type=Unk2 + Vector3 to
// CharacterLookAtTargetParam.Unk10 does NOT drive the head IK at all — the
// values persist (game doesn't stomp them) but the IK consumer ignores them
// for free-look gaze.
//
// This module now probes multiple paths via CameraHeadLookMode:
//   0 = TargetParam Unk2 (original — confirmed no effect)
//   1 = TargetParam Unk3 (untested alt)
//   2 = TargetParam GameObjectId — copy the player's current hard target into the slot;
//       proves whether the IK reads these slots at all when given a real target
//   3 = BannerFollow — set LookAtContainer.BannerCameraFollowFlag = Head|Eyes
//       and write the camera's world position into LookAtContainer.CameraVector
//       (documented "head follows camera" path for banner editor)
//   4 = IsFacingCamera — set LookAtContainer.IsFacingCamera bit + write CameraVector
//
// Whichever mode visibly turns the head is the IK input we hook from for the
// real implementation. If none work, head IK must be driven via the skeleton
// directly (Brio-style bone rotation).
public static unsafe class HeadTracker
{
    private const int SLOT_TORSO = 0;
    private const int SLOT_HEAD  = 1;
    private const int SLOT_EYES  = 2;
    private const int TARGET_PARAM_VEC3_OFFSET = 16;

    private static float   _lastH = float.NaN;
    private static float   _lastV = float.NaN;
    private static Vector3 _cachedPos;     // world point projected from player head along camera-forward
    private static Vector3 _cachedCamPos;  // world camera position (for BannerFollow / IsFacingCamera)
    private static float   _cachedFadeT;   // 0=full override, 1=neutral (cone-falloff)

    private static bool _wasEnabled;
    private static bool _waitingForTarget;
    private static int  _primeDelayFrames;
    private static int  _settleFrames;
    private static bool _wasInEvent;
    // Detect model redraw (teleport / glamour / transformation): when the Character*
    // pointer changes, the LookAt controller has been rebuilt fresh and needs re-prime.
    private static IntPtr _lastCharAddress;

    // Banner-follow state: we restore the original flag value when probing flips off,
    // so toggling modes mid-session doesn't leave stale flags.
    private static byte _origBannerFlag = 0xFF;       // 0xFF = not captured yet
    private static byte _origFaceCameraFlag = 0xFF;

    private static int   _diagTick;
    private static int   _diagWriteCount;
    private static long  _diagFrames;
    private static string? _diagPath;
    private static string _diagTargetSource = "?";
    private static ulong  _diagTargetId;

    public static void Update()
    {
        var cfg = noWickyXIV.Config;
        _diagFrames++;

        bool diag = cfg.CameraHeadLookDiag;
        _diagTick++;
        bool diagThisTick = diag && (_diagFrames <= 5 || _diagTick % 30 == 0);

        if (!cfg.EnableCameraHeadLook)
        {
            if (_wasEnabled) _wasEnabled = false;  // reset prime state on disable
            if (diagThisTick) DiagWrite($"SKIP: disabled (frames={_diagFrames})");
            return;
        }
        if (!_wasEnabled)
        {
            _wasEnabled = true;
            if (cfg.CameraHeadLookAutoPrime)
            {
                cfg.CameraHeadLookMode = 2;
                try { cfg.Save(); } catch { }
                _waitingForTarget = true;
                _primeDelayFrames = 5;
            }
        }

        bool inCombat = false;
        try { inCombat = DalamudApi.Condition[ConditionFlag.InCombat]; } catch { }
        if (cfg.CameraHeadLookDisableInCombat && inCombat)
        {
            if (diagThisTick) DiagWrite("SKIP: in combat");
            return;
        }

        var lp = DalamudApi.ObjectTable.LocalPlayer;
        if (lp == null) { if (diagThisTick) DiagWrite("SKIP: LocalPlayer null"); return; }
        var chr = (Character*)lp.Address;
        if (chr == null) { if (diagThisTick) DiagWrite("SKIP: Character* null"); return; }

        // Redraw detection: Character* swaps after teleport / glamour / certain
        // transformations — the LookAt controller is rebuilt fresh and needs re-prime.
        if (lp.Address != _lastCharAddress)
        {
            _lastCharAddress = lp.Address;
            if (_wasEnabled && cfg.CameraHeadLookAutoPrime)
            {
                cfg.CameraHeadLookMode = 2;
                try { cfg.Save(); } catch { }
                _waitingForTarget = true;
                _primeDelayFrames = 5;
                if (diagThisTick) DiagWrite("REPRIME: Character* changed (redraw/teleport)");
            }
        }

        // NPC dialogue / event resets the game's LookAt IK state.
        // Re-prime when exiting an event so head tracking resumes.
        bool inEvent = false;
        try
        {
            inEvent = DalamudApi.Condition[ConditionFlag.OccupiedInEvent]
                   || DalamudApi.Condition[ConditionFlag.OccupiedInQuestEvent]
                   || DalamudApi.Condition[ConditionFlag.OccupiedInCutSceneEvent];
        }
        catch { }
        if (_wasInEvent && !inEvent && cfg.CameraHeadLookAutoPrime && !_waitingForTarget)
        {
            cfg.CameraHeadLookMode = 2;
            try { cfg.Save(); } catch { }
            _waitingForTarget = true;
            _primeDelayFrames = 5;
            if (diagThisTick) DiagWrite("REPRIME: exited NPC event");
        }
        _wasInEvent = inEvent;

        var cam = Common.CameraManager;
        if (cam == null) { if (diagThisTick) DiagWrite("SKIP: CameraManager null"); return; }
        var wc = cam->worldCamera;
        if (wc == null) { if (diagThisTick) DiagWrite("SKIP: worldCamera null"); return; }
        if (wc->mode != 1) { if (diagThisTick) DiagWrite($"SKIP: camera mode={wc->mode}"); return; }

        float h = wc->currentHRotation;
        float v = wc->currentVRotation;

        // ALWAYS recompute — the cached _cachedPos is world-space, so when the player
        // walks but the camera doesn't move, an epsilon-debounced cache becomes stale
        // and the head visibly drifts toward an absolute world point the player has now
        // walked past. The trig is a few cycles per frame; cheap enough to do every tick.
        bool recomputed = true;
        {
            // Apply pitch baseline so V==pitchOffset → horizontal head aim.
            float effV = v - cfg.CameraHeadLookPitchOffset;
            float ch = MathF.Cos(h), sh = MathF.Sin(h);
            float cv = MathF.Cos(effV), sv = MathF.Sin(effV);
            float ySign = cfg.CameraHeadLookInvertV ? -1f : 1f;
            // X/Z negated: at yaw=H, the camera POSITION is at (sin H, cos H) from player,
            // so camera-look (from camera through player and outward) is the opposite.
            var forward = new Vector3(-sh * cv, ySign * sv, -ch * cv);

            var head = new Vector3(lp.Position.X, lp.Position.Y + 1.5f, lp.Position.Z);

            // Sensitivity: scale the angular deviation from neutral (player-forward).
            // sens < 1 = subtler head turns, sens > 1 = exaggerated.
            float sens = cfg.CameraHeadLookSensitivity;
            var rawTarget = head + forward * cfg.CameraHeadLookDistance;

            // Cone falloff: when camera yaw deviates from player-facing past the limit,
            // fade target toward "look along player-facing" neutral so the IK clamp stops
            // fighting our writes at extreme angles.
            float playerRot = lp.Rotation;
            var neutral = head + new Vector3(-MathF.Sin(playerRot), 0f, -MathF.Cos(playerRot)) * cfg.CameraHeadLookDistance;

            // Apply sensitivity as a blend between neutral and raw target.
            var targetPos = Vector3.Lerp(neutral, rawTarget, MathF.Min(sens, 1f));
            if (sens > 1f)
                targetPos = rawTarget + (rawTarget - neutral) * (sens - 1f);

            float yawDelta  = MathF.Abs(WrapPi(h - (playerRot + MathF.PI)));
            float over = yawDelta - cfg.CameraHeadLookConeLimit;
            if (over <= 0f)
                _cachedFadeT = 0f;
            else
                _cachedFadeT = MathF.Min(1f, over / MathF.Max(0.05f, cfg.CameraHeadLookConeFalloff));

            if (_cachedFadeT > 0f)
                targetPos = Vector3.Lerp(targetPos, neutral, _cachedFadeT);

            // Smooth: lerp _cachedPos toward targetPos for fluid head movement.
            // First frame after reload: snap directly (lerp from zero is wrong).
            if (_cachedPos == Vector3.Zero)
            {
                _cachedPos = targetPos;
            }
            else
            {
                float dt = 1f / 60f;
                float t = 1f - MathF.Exp(-cfg.CameraHeadLookSmoothing * dt);
                _cachedPos = Vector3.Lerp(_cachedPos, targetPos, t);
            }

            _cachedCamPos = new Vector3(wc->viewX, wc->viewY, wc->viewZ);
            _lastH = h; _lastV = v;
        }

        var ctrl = &chr->LookAt.Controller;
        int paramCount = ctrl->ParamCount;
        var paramsSpan = ctrl->Params;
        int spanLen = paramsSpan.Length;

        if (paramCount <= 0 && cfg.CameraHeadLookMode < 3)
        {
            if (diagThisTick) DiagWrite($"SKIP: ParamCount={paramCount} spanLen={spanLen}");
            return;
        }

        string preSnap = diagThisTick ? SnapshotEverything(ctrl, paramsSpan, spanLen, chr) : "";

        bool wroteThisFrame = false;
        int mode = cfg.CameraHeadLookMode;

        // Reset the diag target tag each frame; the mode 2 / prime branches set it.
        _diagTargetSource = "-";
        _diagTargetId = 0;

        if (_waitingForTarget)
        {
            if (_primeDelayFrames > 0)
            {
                _primeDelayFrames--;
            }
            else if (_primeDelayFrames == 0)
            {
                ClickTranslator.SendKey(0x25); // VK_LEFT — cycle target
                _primeDelayFrames = -1;
                if (diagThisTick) DiagWrite("PRIME: sent synthetic VK_LEFT");
            }
            else if (_settleFrames > 0)
            {
                _settleFrames--;
                if (_settleFrames == 0)
                {
                    _waitingForTarget = false;
                    cfg.CameraHeadLookMode = 1;
                    try { cfg.Save(); } catch { }
                    mode = 1;
                    if (diagThisTick) DiagWrite("PRIME: settle done, switched to mode 1");
                }
            }
            else
            {
                var tm = DalamudApi.TargetManager;
                if (tm.Target != null)
                {
                    _settleFrames = 30;
                    if (diagThisTick) DiagWrite($"PRIME: target detected ({(ulong)tm.Target.GameObjectId}), settling 30 frames on mode 2");
                }
            }
        }

        if (!cfg.CameraHeadLookObserveOnly)
        {
            switch (mode)
            {
                case 0: case 1:
                {
                    // Clear any stale BannerCameraFollow / IsFacingCamera flags from prior
                    // Mode 3/4 tests — those container-level flags override the Params[]
                    // IK path and would prevent the head from following our Unk3/Unk2 writes.
                    chr->LookAt.BannerCameraFollowFlag = LookAtContainer.BannerCameraFollowFlags.None;
                    chr->LookAt.IsFacingCamera = false;

                    var type = (mode == 1)
                        ? CharacterLookAtTargetParam.TargetInfoType.Unk3
                        : CharacterLookAtTargetParam.TargetInfoType.Unk2;
                    if (cfg.CameraHeadLookAffectTorso && spanLen > SLOT_TORSO)
                        WriteWorldPos(ref paramsSpan[SLOT_TORSO].TargetParam, type, _cachedPos);
                    if (cfg.CameraHeadLookAffectHead && spanLen > SLOT_HEAD)
                        WriteWorldPos(ref paramsSpan[SLOT_HEAD].TargetParam, type, _cachedPos);
                    if (cfg.CameraHeadLookAffectEyes && spanLen > SLOT_EYES)
                        WriteWorldPos(ref paramsSpan[SLOT_EYES].TargetParam, type, _cachedPos);
                    break;
                }
                case 2:
                {
                    // Same banner-flag scrub as mode 0/1 — keeps the IK reading Params[].
                    chr->LookAt.BannerCameraFollowFlag = LookAtContainer.BannerCameraFollowFlags.None;
                    chr->LookAt.IsFacingCamera = false;

                    // Probe: write player's current target (hard → soft fallback) ID into
                    // the slot. If the head tracks the target while this mode is on, the
                    // IK reads these slots for GameObjectId paths.
                    var tm = DalamudApi.TargetManager;
                    var tgt = tm.Target ?? tm.SoftTarget;
                    ulong tid = tgt != null ? (ulong)tgt.GameObjectId : 0UL;
                    _diagTargetSource = tm.Target != null ? "hard"
                                       : tm.SoftTarget != null ? "soft" : "NONE";
                    _diagTargetId = tid;
                    if (cfg.CameraHeadLookAffectTorso && spanLen > SLOT_TORSO)
                        WriteTargetId(ref paramsSpan[SLOT_TORSO].TargetParam, tid);
                    if (cfg.CameraHeadLookAffectHead && spanLen > SLOT_HEAD)
                        WriteTargetId(ref paramsSpan[SLOT_HEAD].TargetParam, tid);
                    if (cfg.CameraHeadLookAffectEyes && spanLen > SLOT_EYES)
                        WriteTargetId(ref paramsSpan[SLOT_EYES].TargetParam, tid);
                    break;
                }
                case 3:
                {
                    // BannerFollow: set the documented head/eye-follow-camera flag and
                    // write the camera position into CameraVector. If the banner editor
                    // path is live in normal gameplay, the head/eyes turn toward the camera.
                    var container = &chr->LookAt;
                    if (_origBannerFlag == 0xFF) _origBannerFlag = (byte)container->BannerCameraFollowFlag;
                    byte flag = 0;
                    if (cfg.CameraHeadLookAffectHead) flag |= (byte)LookAtContainer.BannerCameraFollowFlags.Head;
                    if (cfg.CameraHeadLookAffectEyes) flag |= (byte)LookAtContainer.BannerCameraFollowFlags.Eyes;
                    container->BannerCameraFollowFlag = (LookAtContainer.BannerCameraFollowFlags)flag;
                    container->CameraVector = _cachedCamPos;
                    break;
                }
                case 4:
                {
                    // IsFacingCamera (/facecamera-style): set the bit, write camera position.
                    var container = &chr->LookAt;
                    if (_origFaceCameraFlag == 0xFF) _origFaceCameraFlag = container->FaceCameraFlag;
                    container->IsFacingCamera = true;
                    container->CameraVector = _cachedCamPos;
                    break;
                }
            }
            _diagWriteCount++;
            wroteThisFrame = true;
        }
        else
        {
            // Observe-only: restore any flags we may have toggled in a previous mode so we
            // genuinely see the game's natural state.
            if (_origBannerFlag != 0xFF)
            {
                chr->LookAt.BannerCameraFollowFlag = (LookAtContainer.BannerCameraFollowFlags)_origBannerFlag;
                _origBannerFlag = 0xFF;
            }
            if (_origFaceCameraFlag != 0xFF)
            {
                chr->LookAt.FaceCameraFlag = _origFaceCameraFlag;
                _origFaceCameraFlag = 0xFF;
            }
        }

        if (diagThisTick)
        {
            string postSnap = SnapshotEverything(ctrl, paramsSpan, spanLen, chr);
            var sb = new StringBuilder();
            sb.Append(wroteThisFrame ? "WRITE: " : "OBSERVE: ")
              .Append("mode=").Append(mode)
              .Append(" frame=").Append(_diagFrames)
              .Append(" wrote=").Append(_diagWriteCount)
              .Append(" h=").Append(h.ToString("F3"))
              .Append(" v=").Append(v.ToString("F3"))
              .Append(" recomp=").Append(recomputed)
              .Append(" pos=(").Append(_cachedPos.X.ToString("F2"))
              .Append(",").Append(_cachedPos.Y.ToString("F2"))
              .Append(",").Append(_cachedPos.Z.ToString("F2")).Append(")")
              .Append(" camPos=(").Append(_cachedCamPos.X.ToString("F2"))
              .Append(",").Append(_cachedCamPos.Y.ToString("F2"))
              .Append(",").Append(_cachedCamPos.Z.ToString("F2")).Append(")")
              .Append(" playerPos=(").Append(lp.Position.X.ToString("F2"))
              .Append(",").Append(lp.Position.Y.ToString("F2"))
              .Append(",").Append(lp.Position.Z.ToString("F2")).Append(")")
              .Append(" paramCount=").Append(paramCount)
              .Append(" spanLen=").Append(spanLen)
              .Append(" tgtSrc=").Append(_diagTargetSource)
              .Append(" tgtId=").Append(_diagTargetId)
              .Append("\n  pre:  ").Append(preSnap)
              .Append("\n  post: ").Append(postSnap);
            DiagWrite(sb.ToString());
        }
    }

    private static void WriteWorldPos(
        ref CharacterLookAtTargetParam tp,
        CharacterLookAtTargetParam.TargetInfoType type,
        Vector3 pos)
    {
        tp.Type = type;
        fixed (CharacterLookAtTargetParam* p = &tp)
            *(Vector3*)((byte*)p + TARGET_PARAM_VEC3_OFFSET) = pos;
    }

    private static void WriteTargetId(ref CharacterLookAtTargetParam tp, ulong id)
    {
        tp.Type = CharacterLookAtTargetParam.TargetInfoType.GameObjectId;
        fixed (CharacterLookAtTargetParam* p = &tp)
            *(ulong*)((byte*)p + TARGET_PARAM_VEC3_OFFSET) = id;
    }

    private static string SnapshotEverything(
        CharacterLookAtController* ctrl,
        Span<CharacterLookAtControlParam> sp,
        int len,
        Character* chr)
    {
        var sb = new StringBuilder();
        string[] names = { "Torso", "Head", "Eyes", "S3", "S4", "S5" };
        for (int i = 0; i < len && i < names.Length; i++)
        {
            ref var tp = ref sp[i].TargetParam;
            Vector3 vec;
            ulong tidVal;
            fixed (CharacterLookAtTargetParam* tpp = &tp)
            {
                vec = *(Vector3*)((byte*)tpp + TARGET_PARAM_VEC3_OFFSET);
                tidVal = *(ulong*)((byte*)tpp + TARGET_PARAM_VEC3_OFFSET);
            }
            sb.Append('[').Append(names[i]).Append(' ').Append(tp.Type)
              .Append(" tid=").Append(tidVal)
              .Append(" vec=(").Append(vec.X.ToString("F2"))
              .Append(',').Append(vec.Y.ToString("F2"))
              .Append(',').Append(vec.Z.ToString("F2")).Append(")] ");
        }
        // Container-level flags so we can see banner/face state too.
        sb.Append(" || ContainerFlags: BannerCamFollow=")
          .Append((int)chr->LookAt.BannerCameraFollowFlag)
          .Append(" IsFacingCamera=").Append(chr->LookAt.IsFacingCamera ? 1 : 0)
          .Append(" CameraVec=(").Append(chr->LookAt.CameraVector.X.ToString("F2"))
          .Append(',').Append(chr->LookAt.CameraVector.Y.ToString("F2"))
          .Append(',').Append(chr->LookAt.CameraVector.Z.ToString("F2")).Append(")");
        return sb.ToString();
    }

    private static void DiagWrite(string line)
    {
        try
        {
            if (_diagPath == null)
            {
                _diagPath = System.IO.Path.Combine(
                    DalamudApi.PluginInterface.GetPluginConfigDirectory(),
                    "head_diag.txt");
                System.IO.File.WriteAllText(_diagPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] head_diag started\n");
            }
            System.IO.File.AppendAllText(_diagPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n");
        }
        catch { }
    }

    public static void ResetDiag()
    {
        _diagPath = null;
        _diagFrames = 0;
        _diagWriteCount = 0;
        _diagTick = 0;
    }

    private static float WrapPi(float a)
    {
        while (a >  MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }
}
