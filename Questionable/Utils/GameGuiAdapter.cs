using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
namespace Questionable.Utils;

internal unsafe interface IGameGuiAdapter
{
    bool TryGetAddonByName(string name, out AtkUnitBase* addon);
    bool TryGetAddonByName<TAddon>(string name, out TAddon* addon) where TAddon : unmanaged;
}

internal sealed unsafe class LLibGameGuiAdapter(IGameGui gameGui) : IGameGuiAdapter
{
    public bool TryGetAddonByName(string name, out AtkUnitBase* addon)
    {
        nint a = gameGui.GetAddonByName(name, 1);
        if (a != nint.Zero)
        {
            addon = (AtkUnitBase*)a;
            return true;
        }

        addon = null;
        return false;
    }

    public bool TryGetAddonByName<TAddon>(string name, out TAddon* addon) where TAddon : unmanaged
    {
        nint a = gameGui.GetAddonByName(name, 1);
        if (a != nint.Zero)
        {
            addon = (TAddon*)a;
            return true;
        }

        addon = null;
        return false;
    }
}
