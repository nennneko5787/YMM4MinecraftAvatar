using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;

namespace YMM4MinecraftAvatar;

public class MinecraftAvatarPlugin : IShapePlugin
{
    public string Name => "Minecraftアバター";
    public bool IsExoShapeSupported => false;
    public bool IsExoMaskSupported => false;

    public IShapeParameter CreateShapeParameter(SharedDataStore? sharedData)
        => new MinecraftAvatarShapeParameter(sharedData);
}
