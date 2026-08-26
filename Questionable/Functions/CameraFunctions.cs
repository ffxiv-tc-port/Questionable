//Taken and adapted from https://github.com/awgil/ffxiv_navmesh/blob/master/vnavmesh/Movement/OverrideCamera.cs.

using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Microsoft.Extensions.Logging;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
namespace Questionable.Functions;

// TC's client predates the game patch that added api13 - use the pre-api13 offsets,
// same as vnavmesh's CameraEx (Movement/OverrideCamera.cs), since this is the same
// underlying native struct FFXIVClientStructs.FFXIV.Client.Game.Camera doesn't expose here.
[StructLayout(LayoutKind.Explicit, Size = 0x2B0)]
internal unsafe struct CameraEx
{
    [FieldOffset(0x130)] public float DirH;
    [FieldOffset(0x134)] public float DirV;
    [FieldOffset(0x140)] public float InputDeltaH;
    [FieldOffset(0x144)] public float InputDeltaV;
}

internal sealed unsafe class CameraFunctions : IDisposable
{
    private readonly ILogger<CameraFunctions> _logger;
    private readonly IObjectTable _objectTable;

    private readonly bool IgnoreUserInput = true; // if true - override even if user tries to change camera orientation, otherwise override only if user does nothing
    // Global's function-prologue signature doesn't match TC's compiled shape of this function at all.
    // This call-site signature instead is sourced from vnavmesh's own CameraEx hook (Movement/OverrideCamera.cs),
    // confirmed to match exactly once in TC's binary. Kept fallible, same as vnavmesh, so a future signature
    // drift only disables camera auto-facing instead of crashing the whole plugin.
    [Signature("E8 ?? ?? ?? ?? EB 05 E8 ?? ?? ?? ?? 44 0F 28 4C 24 ??", Fallibility = Fallibility.Fallible)]
    private Hook<RMICameraDelegate>? _rmiCameraHook;
    private float DesiredAltitude;
    private float DesiredAzimuth;

    public CameraFunctions(IGameInteropProvider gameInteropProvider, ILogger<CameraFunctions> logger, IObjectTable objectTable)
    {
        _logger = logger;
        gameInteropProvider.InitializeFromAttributes(this);
        _objectTable = objectTable;
        if (_rmiCameraHook == null)
        {
            _logger.LogWarning("RMICamera signature not found - camera auto-facing disabled");
        }
    }

    public bool Enabled
    {
        get => _rmiCameraHook?.IsEnabled ?? false;
        set
        {
            if (_rmiCameraHook == null)
            {
                return;
            }

            if (value)
            {
                _rmiCameraHook.Enable();
            }
            else
            {
                _rmiCameraHook.Disable();
            }
        }
    }

    public void Dispose()
    {
        _rmiCameraHook?.Dispose();
    }

    private static float Deg2Rad(int degrees)
    {
        return degrees * ((float)Math.PI / 180f);
    }

    // from https://github.com/NightmareXIV/ECommons/blob/master/ECommons/MathHelpers/Angle.cs
    private static float Normalized(float r)
    {
        while(r < -MathF.PI)
        {
            r += 2 * MathF.PI;
        }
        while(r > MathF.PI)
        {
            r -= 2 * MathF.PI;
        }
        return r;
    }


    internal void Face(Vector3 pos)
    {
        _logger.LogDebug("Facing " + pos);
        Enabled = true;
        if (_objectTable[0] == null)
        {
            return;
        }
        Vector3 diff = pos - _objectTable[0]!.Position;
        DesiredAzimuth = MathF.Atan2(diff.X, diff.Z) + Deg2Rad(180);
        DesiredAltitude = Deg2Rad(-30);
    }

    private void RMICameraDetour(CameraEx* self, int inputMode, float speedH, float speedV)
    {
        _rmiCameraHook!.Original(self, inputMode, speedH, speedV);
        if (IgnoreUserInput || inputMode == 0) // let user override...
        {
            float dt = Framework.Instance()->FrameDeltaTime;
            float deltaH = Normalized(DesiredAzimuth - self->DirH);
            float deltaV = Normalized(DesiredAltitude - self->DirV);
            float maxH = Deg2Rad(180);
            float maxV = Deg2Rad(180);
            self->InputDeltaH = Math.Clamp(deltaH, -maxH, maxH);
            self->InputDeltaV = Math.Clamp(deltaV, -maxV, maxV);
            Enabled = false;
        }
    }

    private delegate void RMICameraDelegate(CameraEx* self, int inputMode, float speedH, float speedV);
}
