using System.Collections.Generic;
using System.Text;

namespace CustomFonts;

/// <summary>
/// The rest of the game's UI the plugin reads, at the offsets the game's code says (<see cref="GameLayout"/>): the loaded
/// units and their node trees, resource handles' file names and Kernel textures. Each group is resolved by the part of the
/// plugin that uses it.
/// </summary>
internal static unsafe class GameUi
{
    /// <summary>AtkResNode.Type of a text node; types of 1000 and up are component nodes.</summary>
    public const ushort TextNodeType = 3;

    public const ushort FirstComponentNodeType = 1000;

    private static nint stageInstance;
    private static int stageUnitManager;
    private static int loadedUnitsCount;
    private static int loadedUnitsEntries;
    private static int unitUld;
    private static int uldNodeList;
    private static int uldNodeListCount;
    private static int nodeType;
    private static int componentNodeComponent;
    private static int componentUld;

    private static int fileName;
    private static int fileNameLength;
    private static int fileNameCapacity;

    private static int textureD3D11Texture2D;

    /// <summary>Gets AtkStage, or 0 before it is made.</summary>
    public static nint Stage => *(nint*)stageInstance;

    /// <summary>Resolves the loaded units and their node trees: <see cref="ForEachLoadedUnit"/> and the node accessors.</summary>
    public static void ResolveUnits()
    {
        stageInstance = GameLayout.Target("AtkStage.Instance");
        stageUnitManager = GameLayout.Get("AtkStage.RaptureAtkUnitManager");
        loadedUnitsCount = GameLayout.Get("AtkUnitManager.AllLoadedUnitsList.Count");
        loadedUnitsEntries = GameLayout.Get("AtkUnitManager.AllLoadedUnitsList.Entries");
        unitUld = GameLayout.Get("AtkUnitBase.UldManager");
        uldNodeList = GameLayout.Get("AtkUldManager.NodeList");
        uldNodeListCount = GameLayout.Get("AtkUldManager.NodeListCount");
        nodeType = GameLayout.Get("AtkResNode.Type");
        componentNodeComponent = GameLayout.Get("AtkComponentNode.Component");
        componentUld = GameLayout.Get("AtkComponentBase.UldManager");
    }

    /// <summary>Resolves <see cref="GetFileName"/>.</summary>
    public static void ResolveResourceHandles()
    {
        fileName = GameLayout.Get("ResourceHandle.FileName");
        fileNameLength = GameLayout.Get("ResourceHandle.FileName.Length");
        fileNameCapacity = GameLayout.Get("ResourceHandle.FileName.Capacity");
    }

    /// <summary>Resolves <see cref="GetD3D11Texture"/>.</summary>
    public static void ResolveTextures() => textureD3D11Texture2D = GameLayout.Get("Texture.D3D11Texture2D");

    /// <summary>Gets every loaded unit (AtkUnitBase*).</summary>
    public static List<nint> GetLoadedUnits()
    {
        var units = new List<nint>();
        var stage = Stage;
        var manager = stage == 0 ? 0 : *(nint*)(stage + stageUnitManager);
        if (manager == 0)
            return units;
        var count = *(ushort*)(manager + loadedUnitsCount);
        var entries = (nint*)(manager + loadedUnitsEntries);
        for (var i = 0; i < count; i++)
        {
            if (entries[i] != 0)
                units.Add(entries[i]);
        }

        return units;
    }

    /// <summary>Gets the ULD manager of every loaded unit.</summary>
    public static List<nint> GetLoadedUnitUlds() => GetLoadedUnits().ConvertAll(unit => unit + unitUld);

    public static int GetNodeCount(nint uld) => *(nint*)(uld + uldNodeList) == 0 ? 0 : *(ushort*)(uld + uldNodeListCount);

    public static nint GetNode(nint uld, int index) => (*(nint**)(uld + uldNodeList))[index];

    public static ushort GetNodeType(nint node) => *(ushort*)(node + nodeType);

    /// <summary>Gets the ULD manager of a component node's component; 0 if it has none.</summary>
    public static nint GetComponentUld(nint componentNode)
    {
        var component = *(nint*)(componentNode + componentNodeComponent);
        return component == 0 ? 0 : component + componentUld;
    }

    /// <summary>Gets a resource handle's file name (an MSVC std::string: inline while it is shorter than 16).</summary>
    public static string GetFileName(nint handle)
    {
        var length = *(ulong*)(handle + fileNameLength);
        var capacity = *(ulong*)(handle + fileNameCapacity);
        var text = capacity < 16 ? (byte*)(handle + fileName) : *(byte**)(handle + fileName);
        return text is null || length > 4096 ? string.Empty : Encoding.UTF8.GetString(text, (int)length);
    }

    /// <summary>Gets a Kernel::Texture's ID3D11Texture2D.</summary>
    public static nint GetD3D11Texture(nint kernelTexture) => *(nint*)(kernelTexture + textureD3D11Texture2D);
}
